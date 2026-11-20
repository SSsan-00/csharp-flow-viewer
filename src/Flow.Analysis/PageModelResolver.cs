using Flow.Model;
using Microsoft.CodeAnalysis;

namespace Flow.Analysis;

/// <summary>
/// Connects a parsed Razor page to source handlers in the already loaded project.
/// This is source analysis only: PageModel constructors, handlers and Tag Helpers are never executed.
/// </summary>
internal static class PageModelResolver
{
    public static async Task<RazorSourceFile> ReadAsync(Project project, ProjectFile file,
        IReadOnlyList<ProjectFile> files, CancellationToken token)
    {
        if (!file.IsRazor || !files.Contains(file))
            throw new ProjectLoadException("対象の Razor ファイルが見つかりません。");

        var notices = new List<string>();
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project.FilePath!))!;
        if (!Path.GetRelativePath(projectDirectory, file.Path).IsWithinDirectory())
            notices.Add("解析は部分的 · プロジェクト外のリンクされた Razor ファイルでは、論理パスに基づく _ViewImports を解決していません。ページ内のディレクティブを基に解析します。");
        var text = await File.ReadAllTextAsync(file.Path, token).ConfigureAwait(false);
        var imports = await ReadImportsAsync(project, file.Path, token).ConfigureAwait(false);
        var page = new RazorSourceFile(text, file.Path, imports, token, new LocalJavaScriptFiles(project.FilePath!, files));
        if (!page.IsPage)
        {
            page.BindHandlers([], notices);
            return page;
        }

        var compilation = await project.GetCompilationAsync(token).ConfigureAwait(false);
        if (compilation is null)
        {
            notices.Add("解析は部分的 · PageModel のコンパイル情報を取得できません。");
            page.BindHandlers([], notices);
            return page;
        }

        var model = ResolveModel(compilation, page, file, notices, token);
        var handlers = model is null ? [] : FindHandlers(model, files, notices, token);
        page.BindHandlers(handlers, notices);
        return page;
    }

    /// <summary>Reads inherited imports in parent-to-child order, stopping at the project directory.</summary>
    private static async Task<IReadOnlyList<RazorImport>> ReadImportsAsync(Project project, string filePath,
        CancellationToken token)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(project.FilePath!))!;
        var directories = new Stack<string>();
        for (var directory = Path.GetDirectoryName(filePath); directory is not null;
            directory = Path.GetDirectoryName(directory))
        {
            if (!Path.GetRelativePath(root, directory).IsWithinDirectory()) break;
            directories.Push(directory);
            if (PathEquals(root, directory)) break;
        }
        var imports = new List<RazorImport>();
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "_ViewImports.cshtml");
            if (PathEquals(path, filePath) || !File.Exists(path)) continue;
            imports.Add(new RazorImport(path, await File.ReadAllTextAsync(path, token).ConfigureAwait(false)));
        }
        return imports;
    }

    private static INamedTypeSymbol? ResolveModel(Compilation compilation, RazorSourceFile page,
        ProjectFile file, List<string> notices, CancellationToken token)
    {
        var name = page.ModelName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            notices.Add("解析は部分的 · @model の型を特定できないため、ハンドラーへの移動を作成できません。");
            return null;
        }

        var isGlobal = name.StartsWith("global::", StringComparison.Ordinal);
        if (isGlobal) name = name["global::".Length..];
        var candidates = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        if (!isGlobal && !name.Contains('<'))
        {
            // A type in the nearest namespace hides equally named types in outer namespaces
            // and imports. Combining every scope would manufacture ambiguity for valid pages.
            for (var scope = page.NamespaceName; !string.IsNullOrWhiteSpace(scope);)
            {
                AddType(scope + "." + name);
                if (candidates.Count > 0) break;
                var dot = scope.LastIndexOf('.');
                scope = dot < 0 ? null : scope[..dot];
            }
        }
        if (candidates.Count == 0) AddType(name);
        if (!isGlobal && !name.Contains('<') && candidates.Count == 0)
        {
            // Imports share precedence: retain all matches so a genuinely ambiguous name
            // produces a diagnostic instead of an arbitrary handler link.
            foreach (var import in page.Usings)
            {
                var scope = import.Trim().Replace("global::", "", StringComparison.Ordinal);
                if (!scope.Contains('=') && !scope.StartsWith("static ", StringComparison.Ordinal))
                    AddType(scope.TrimEnd(';') + "." + name);
            }
            if (candidates.Count == 0 && !name.Contains('.'))
            {
                // The default Razor namespace may depend on the target SDK. Only accept a uniquely named
                // PageModel declared in the conventional companion; never choose another same-named type.
                var pairedPath = file.Path + ".cs";
                foreach (var symbol in compilation.GetSymbolsWithName(name, SymbolFilter.Type, token).OfType<INamedTypeSymbol>())
                    if (symbol.DeclaringSyntaxReferences.Any(reference => PathEquals(reference.SyntaxTree.FilePath, pairedPath)))
                        candidates.Add(symbol);
            }
        }

        if (candidates.Count != 1)
        {
            notices.Add(candidates.Count == 0
                ? $"解析は部分的 · @model {name} を解決できません。参照、@using、@namespace を確認してください。"
                : $"解析は部分的 · @model {name} の候補が複数あります。ハンドラーを一つに決められません。");
            return null;
        }

        var model = candidates.Single();
        for (var type = model; type is not null; type = type.BaseType)
            if (type.ToDisplayString() == "Microsoft.AspNetCore.Mvc.RazorPages.PageModel") return model;
        notices.Add($"解析は部分的 · {name} が PageModel を継承していることを確認できません。");
        return null;

        void AddType(string metadataName)
        {
            if (compilation.GetTypeByMetadataName(metadataName) is { } type) candidates.Add(type);
        }
    }

    /// <summary>Collects source-backed handler candidates without guessing between overloads or HTTP handlers.</summary>
    private static IReadOnlyList<RazorHandler> FindHandlers(INamedTypeSymbol model,
        IReadOnlyList<ProjectFile> files, List<string> notices, CancellationToken token)
    {
        var result = new List<RazorHandler>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var type = model; type is not null && type.ToDisplayString() != "Microsoft.AspNetCore.Mvc.RazorPages.PageModel";
            type = type.BaseType)
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            token.ThrowIfCancellationRequested();
            var signature = method.Name + "(" + string.Join(",", method.Parameters.Select(parameter =>
                parameter.RefKind + ":" + parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))) + ")";
            if (!seen.Add(signature) || method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.IsAbstract
                || method.DeclaredAccessibility != Accessibility.Public || method.IsGenericMethod
                || IsNonHandler(method)) continue;
            var name = method.Name;
            if (!name.StartsWith("On", StringComparison.Ordinal)) continue;
            if (name.EndsWith("Async", StringComparison.Ordinal)) name = name[..^5];
            var boundary = 3;
            while (boundary < name.Length && char.IsLower(name[boundary])) boundary++;
            if (name.Length < 3 || !char.IsUpper(name[2])) continue;
            var verb = name[2..Math.Min(boundary, name.Length)].ToUpperInvariant();
            var handlerName = boundary >= name.Length ? null : name[boundary..];
            var declaration = (method.PartialImplementationPart ?? method).DeclaringSyntaxReferences.FirstOrDefault();
            if (declaration is null || !files.Any(file => file.Id is not null && PathEquals(file.Path, declaration.SyntaxTree.FilePath)))
            {
                notices.Add($"解析は部分的 · {method.Name} の本体が選択中プロジェクトのソース一覧にありません。");
                continue;
            }
            var syntax = declaration.GetSyntax(token);
            var source = syntax.SyntaxTree.GetText(token);
            var range = new SourceRange(syntax.SpanStart, syntax.Span.Length,
                source.Lines.GetLineFromPosition(syntax.SpanStart).LineNumber + 1, syntax.SyntaxTree.FilePath);
            result.Add(new RazorHandler(method.Name, verb, handlerName, syntax.SyntaxTree.FilePath, syntax.SpanStart, range));
        }
        return result;
    }

    private static bool IsNonHandler(IMethodSymbol method)
    {
        // Reflection inherits NonHandlerAttribute through overrides. Roslyn's GetAttributes
        // returns only attributes declared on a symbol, so inspect the override chain explicitly.
        for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
            if (current.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString()
                == "Microsoft.AspNetCore.Mvc.RazorPages.NonHandlerAttribute")) return true;
        return false;
    }

    private static bool PathEquals(string first, string second) =>
        string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
