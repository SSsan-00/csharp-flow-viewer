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

public sealed record ProjectFile(DocumentId Id, string Label, string Path)
{
    public override string ToString() => Label;
}

public sealed class ProjectSession(MSBuildWorkspace workspace, Project project, string[] notices, string? intermediateDirectory, ProjectOptions options) : IDisposable
{
    public ProjectOptions Options { get; } = options;
    public IReadOnlyList<string> Notices { get; } = notices;
    public IReadOnlyList<ProjectFile> Files { get; } = project.Documents
        .Where(doc => doc.FilePath is { } path && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            && (intermediateDirectory is null || !Path.GetRelativePath(intermediateDirectory, path).IsWithinDirectory()))
        .Select(doc => new ProjectFile(doc.Id,
            Path.GetRelativePath(Path.GetDirectoryName(project.FilePath!)!, doc.FilePath!), doc.FilePath!))
        .OrderBy(file => file.Label, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<SourceFile> ReadAsync(ProjectFile file, CancellationToken token)
    {
        var document = project.GetDocument(file.Id) ?? throw new ProjectLoadException("対象ファイルが見つかりません。");
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
        return new SourceFile(model, diagnostics, token);
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
        var evaluated = await EvaluateAsync(path, properties, token).ConfigureAwait(false);
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
            return new ProjectSession(workspace, project, notices, intermediate, options);
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

    private static async Task<JsonElement> EvaluateAsync(string path, Dictionary<string, string> globals, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        var arguments = new List<string> { "msbuild", path, "-nologo", "-verbosity:quiet",
            "-getProperty:Configuration,Configurations,TargetFramework,TargetFrameworks,BaseIntermediateOutputPath,NETCoreSdkVersion" };
        arguments.AddRange(globals.Select(pair => $"-property:{pair.Key}={pair.Value}"));
        var output = await DotnetAsync(Path.GetDirectoryName(path)!, arguments, token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(output);
            return json.RootElement.GetProperty("Properties").Clone();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            throw new ProjectLoadException("MSBuild のプロジェクト評価結果を読み取れません。SDK 8 以降と対象プロジェクトの設定を確認してください。");
        }
    }

    private static string Property(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

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
