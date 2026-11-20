using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Flow.Analysis;

public sealed class ProjectLoadException(string message) : Exception(message);

public sealed record ProjectOptions(string Configuration, string? TargetFramework)
{
    public override string ToString() => $"{Configuration}  /  {TargetFramework ?? "既定 TFM"}";
}

public sealed record ProjectSettings(string SdkVersion, IReadOnlyList<ProjectOptions> Options);

public sealed record ProjectFile(DocumentId? Id, string Label, string Path)
{
    public bool IsRazor => Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
    public bool IsJavaScript => Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Label;
}

public sealed class ProjectSession(MSBuildWorkspace workspace, Project project, string[] notices, string? intermediateDirectory,
    ProjectOptions options, IReadOnlyList<string>? razorFiles = null, IReadOnlyList<string>? javaScriptFiles = null) : IDisposable
{
    public ProjectOptions Options { get; } = options;
    public IReadOnlyList<string> Notices { get; } = notices;
    public IReadOnlyList<ProjectFile> Files { get; } = project.Documents
        .Where(doc => doc.FilePath is { } path && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            && (intermediateDirectory is null || !Path.GetRelativePath(intermediateDirectory, path).IsWithinDirectory()))
        .Select(doc => new ProjectFile(doc.Id,
            Path.GetRelativePath(Path.GetDirectoryName(project.FilePath!)!, doc.FilePath!), doc.FilePath!))
        .Concat((razorFiles ?? []).Concat(javaScriptFiles ?? []).Where(File.Exists)
            .Where(path => intermediateDirectory is null || !Path.GetRelativePath(intermediateDirectory, path).IsWithinDirectory())
            .Select(path => new ProjectFile(null, Path.GetRelativePath(Path.GetDirectoryName(project.FilePath!)!, path), path)))
        .DistinctBy(file => file.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        .OrderBy(file => file.Label, StringComparer.OrdinalIgnoreCase).ToArray();

    private IReadOnlySet<string>? navigableCSharpPaths;

    private IReadOnlySet<string> NavigableCSharpPaths => navigableCSharpPaths ??= Files
        .Where(file => file.Id is not null).Select(file => file.Path)
        .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task<SourceFile> ReadAsync(ProjectFile file, CancellationToken token)
    {
        var document = file.Id is { } id ? project.GetDocument(id) : null;
        if (document is null) throw new ProjectLoadException("対象の C# ファイルが見つかりません。");
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false)
            ?? throw new ProjectLoadException("コンパイルとセマンティックモデルを取得できません。");
        var diagnostics = model.GetDiagnostics(cancellationToken: token)
            .Concat(model.Compilation.GetDeclarationDiagnostics(token))
            .Where(item => item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .DistinctBy(item => (item.Id, item.Location))
            .GroupBy(item => (item.Id, item.Severity))
            .Select(group =>
            {
                var lines = group.Where(item => item.Location.SourceTree == model.SyntaxTree)
                    .Select(item => item.Location.GetLineSpan().StartLinePosition.Line + 1).Distinct().Take(8).ToArray();
                return $"{(group.Key.Severity == DiagnosticSeverity.Error ? "解析は部分的" : "警告")} · {group.Key.Id} × {group.Count()}" +
                    (lines.Length == 0 ? "" : $"（選択ファイル L{string.Join(", L", lines)}）");
            }).ToList();
        if (workspace.Diagnostics.Any(item => item.Kind == WorkspaceDiagnosticKind.Failure)
            && !Notices.Any(item => item.StartsWith("解析は部分的", StringComparison.Ordinal)))
            diagnostics.Add("解析は部分的 · プロジェクトの参照解決に失敗しました。");
        return new SourceFile(model, diagnostics, token, NavigableCSharpPaths);
    }

    /// <summary>Reads a Razor document without executing it, then resolves its PageModel using the loaded compilation.</summary>
    public Task<RazorSourceFile> ReadRazorAsync(ProjectFile file, CancellationToken token) =>
        PageModelResolver.ReadAsync(project, file, Files, token);

    public Task<JavaScriptSourceFile> ReadJavaScriptAsync(ProjectFile file, CancellationToken token,
        RazorSourceFile? razorContext = null, JavaScriptSourceFile? jsContext = null)
    {
        token.ThrowIfCancellationRequested();
        if (!file.IsJavaScript || !Files.Contains(file))
            throw new ProjectLoadException("対象の JavaScript ファイルが見つかりません。");
        if ((razorContext?.OpenScript(file.Path) ?? jsContext?.OpenScript(file.Path)) is { } existing)
            return Task.FromResult(existing);
        var local = new LocalJavaScriptFiles(project.FilePath!, Files);
        var fragment = local.Read(file.Path, module: false, token)
            ?? throw new ProjectLoadException("JavaScript ファイルを読み込めません。サイズまたはファイルの場所を確認してください。");
        return Task.FromResult(JavaScriptSourceFile.Open(fragment, local, token));
    }

    public void Dispose() => workspace.Dispose();
}

public static class ProjectLoader
{
    public static async Task<ProjectSettings> InspectAsync(string path, CancellationToken token)
    {
        var initial = await EvaluateAsync(path, DesignTimeProperties(), token).ConfigureAwait(false);
        var configuration = Property(initial, "Configuration");
        if (string.IsNullOrWhiteSpace(configuration))
            configuration = "Debug";
        var configurations = new[] { configuration }.Concat(Split(Property(initial, "Configurations")))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var options = new List<ProjectOptions>();
        foreach (var candidate in configurations)
        {
            var globals = DesignTimeProperties();
            globals["Configuration"] = candidate;
            var evaluated = candidate == configuration ? initial
                : await EvaluateAsync(path, globals, token).ConfigureAwait(false);
            var frameworks = Split(Property(evaluated, "TargetFrameworks"))
                .Concat(Split(Property(evaluated, "TargetFramework")))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            options.AddRange(frameworks.DefaultIfEmpty().Select(tfm => new ProjectOptions(candidate, tfm)));
        }
        var sdk = Property(initial, "NETCoreSdkVersion");
        if (sdk.Length == 0)
            sdk = (await DotnetAsync(Path.GetDirectoryName(Path.GetFullPath(path))!, ["--version"], token).ConfigureAwait(false)).Trim();
        return new ProjectSettings(sdk, options);
    }

    public static async Task<ProjectSession> OpenAsync(string path, ProjectOptions options, CancellationToken token)
    {
        var properties = DesignTimeProperties();
        properties["Configuration"] = options.Configuration;
        if (options.TargetFramework is { } tfm)
            properties["TargetFramework"] = tfm;
        var evaluated = await EvaluateAsync(path, properties, token, includeItems: true).ConfigureAwait(false);
        var intermediatePath = Property(evaluated, "BaseIntermediateOutputPath");
        var intermediate = string.IsNullOrWhiteSpace(intermediatePath) ? null :
            Path.GetFullPath(intermediatePath.Replace('\\', Path.DirectorySeparatorChar),
                Path.GetDirectoryName(Path.GetFullPath(path))!);
        var workspace = MSBuildWorkspace.Create(properties);
        try
        {
            var project = await workspace.OpenProjectAsync(path, cancellationToken: token).ConfigureAwait(false);
            var solution = project.Solution;
            foreach (var referenceProject in solution.Projects)
                solution = solution.WithProjectAnalyzerReferences(referenceProject.Id, []);
            project = solution.GetProject(project.Id)!;
            var notices = workspace.Diagnostics.Select(DescribeWorkspaceDiagnostic).Distinct().ToArray();
            return new ProjectSession(workspace, project, notices, intermediate, options, MarkupFiles(evaluated, path, ".cshtml"), MarkupFiles(evaluated, path, ".js"));
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static string DescribeWorkspaceDiagnostic(WorkspaceDiagnostic diagnostic)
    {
        var code = DiagnosticCode(diagnostic.Message);
        var id = code.Success ? $"（{code.Value}）" : "";
        if (diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            return $"解析は部分的 · MSBuild の評価または参照解決に失敗しました{id}。SDK、復元済み依存関係、設計時ビルドを確認してください。";
        return $"MSBuild 警告{id} · 一部のプロジェクト設定や参照が取得できなかった可能性があります。";
    }

    private static Match DiagnosticCode(string message) => Regex.Match(message, @"\b(?:MSB|NETSDK)[0-9]{4}\b");

    private static async Task<JsonElement> EvaluateAsync(string path, Dictionary<string, string> globals, CancellationToken token,
        bool includeItems = false)
    {
        path = Path.GetFullPath(path);
        var arguments = new List<string> { "msbuild", path, "-nologo", "-verbosity:quiet",
            "-getProperty:Configuration,Configurations,TargetFramework,TargetFrameworks,BaseIntermediateOutputPath,NETCoreSdkVersion" };
        // Honor evaluated MSBuild items, including linked files and conditional removals.
        // A directory scan would incorrectly reintroduce pages excluded by the selected project configuration.
        if (includeItems) arguments.Add("-getItem:Content,None,AdditionalFiles,RazorGenerate");
        arguments.AddRange(globals.Select(pair => $"-property:{pair.Key}={pair.Value}"));
        var output = await DotnetAsync(Path.GetDirectoryName(path)!, arguments, token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(output);
            _ = json.RootElement.GetProperty("Properties");
            return json.RootElement.Clone();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            throw new ProjectLoadException("MSBuild のプロジェクト評価結果を読み取れません。SDK 8 以降と対象プロジェクトの設定を確認してください。");
        }
    }

    private static string Property(JsonElement evaluation, string name) =>
        evaluation.GetProperty("Properties").TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    private static string[] MarkupFiles(JsonElement evaluation, string projectPath, string extension)
    {
        if (!evaluation.TryGetProperty("Items", out var items)) return [];
        var root = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var paths = new List<string>();
        foreach (var itemGroup in items.EnumerateObject())
        foreach (var item in itemGroup.Value.EnumerateArray())
        {
            var path = item.TryGetProperty("FullPath", out var fullPath) ? fullPath.GetString()
                : item.TryGetProperty("Identity", out var identity) ? identity.GetString() : null;
            if (path?.EndsWith(extension, StringComparison.OrdinalIgnoreCase) != true) continue;
            var full = Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), root);
            if (extension != ".js" || Path.GetRelativePath(root, full).IsWithinDirectory())
                paths.Add(full);
        }
        return paths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, string> DesignTimeProperties() => new()
    {
        ["DesignTimeBuild"] = "true",
        ["BuildingInsideVisualStudio"] = "true",
        ["SkipCompilerExecution"] = "true",
        ["BuildProjectReferences"] = "false",
        ["NuGetAudit"] = "false",
        ["RestoreDuringBuild"] = "false",
        ["MSBuildEnableWorkloadResolver"] = "false"
    };

    private static string[] Split(string value) =>
        value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static async Task<string> DotnetAsync(string directory, IEnumerable<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        info.Environment["MSBUILDDISABLENUGETSDKRESOLVER"] = "true";
        info.Environment["MSBuildEnableWorkloadResolver"] = "false";
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ProjectLoadException(".NET SDK が見つかりません。対象に必要な SDK をインストールし、dotnet を PATH に追加してください。");
        }
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await Task.WhenAll(output, error, process.WaitForExitAsync(token)).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var code = DiagnosticCode(await error.ConfigureAwait(false) + await output.ConfigureAwait(false));
            throw new ProjectLoadException("MSBuild でプロジェクトを評価できません。" +
                (code.Success ? $"（{code.Value}）" : "") +
                " global.json、インストール済み SDK、プロジェクトのインポートを確認してください。");
        }
        return await output.ConfigureAwait(false);
    }
}

internal static class PathHelpers
{
    public static bool IsWithinDirectory(this string relative) =>
        relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !Path.IsPathRooted(relative);
}
