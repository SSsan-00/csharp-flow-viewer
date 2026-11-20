using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace Flow.Analysis;

public sealed record CallerLocation(ProjectFile File, int? EntryId, SourceRange Source, ProjectFile? RazorContext = null);
public sealed record CallerSearch(IReadOnlyList<CallerLocation> Locations, bool IsPartial = false);

public sealed partial class ProjectSession
{
    private const int CallerLimit = 100;
    private const int JavaScriptFileLimit = 256;

    public async Task<CallerSearch> FindCallersAsync(ProjectFile targetFile, int targetId, CancellationToken token)
    {
        if (!Files.Contains(targetFile)) return new CallerSearch([]);
        if (targetFile.Id is { } documentId && project.GetDocument(documentId) is { } document)
            return await FindCSharpCallersAsync(document, targetId, token).ConfigureAwait(false);

        var locations = new Dictionary<(string Path, int Offset, int Length), CallerLocation>();
        var filesByPath = Files.ToDictionary(file => file.Path, LocalJavaScriptFiles.PathComparer);
        var candidates = Files.Where(file => file.IsRazor || file.IsJavaScript)
            .Where(file => !targetFile.IsRazor || file == targetFile)
            .OrderByDescending(file => file == targetFile).Take(JavaScriptFileLimit + 1).ToArray();
        var partial = candidates.Length > JavaScriptFileLimit;
        foreach (var file in candidates.Take(JavaScriptFileLimit))
        {
            token.ThrowIfCancellationRequested();
            IEnumerable<RazorJavaScript.CallSite> sites;
            try
            {
                sites = file.IsRazor ? (await ReadRazorAsync(file, token).ConfigureAwait(false)).CallSites(token)
                    : (await ReadJavaScriptAsync(file, token).ConfigureAwait(false)).CallSites(token);
                foreach (var site in sites)
                {
                    if (site.Target?.Method != targetId
                        || !LocalJavaScriptFiles.PathComparer.Equals(site.Target.FilePath, targetFile.Path)) continue;
                    if (site.Source.FilePath is not { } path || !filesByPath.TryGetValue(path, out var owner)) continue;
                    var entryId = owner.IsJavaScript ? site.Owner.Source.Offset : site.Owner.Id;
                    locations.TryAdd((owner.Path, site.Source.Offset, site.Source.Length),
                        new CallerLocation(owner, entryId, site.Source, file.IsRazor && owner != file ? file : null));
                    if (locations.Count >= CallerLimit) return SortCallers(locations.Values, true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProjectLoadException)
            {
                partial = true;
            }
        }
        return SortCallers(locations.Values, partial);
    }

    private async Task<CallerSearch> FindCSharpCallersAsync(Document document, int targetId, CancellationToken token)
    {
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (root is null || model is null || !root.FullSpan.Contains(targetId)) return new CallerSearch([]);
        var declaration = root.FindToken(targetId).Parent?.AncestorsAndSelf().FirstOrDefault(node =>
            node.SpanStart == targetId && node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
        if (declaration is null || model.GetDeclaredSymbol(declaration, token) is not IMethodSymbol target)
            return new CallerSearch([]);

        var definition = Canonical(target);
        var filesById = Files.Where(file => file.Id is not null).ToDictionary(file => file.Id!);
        var result = new List<CallerLocation>();
        var references = await SymbolFinder.FindReferencesAsync(definition, project.Solution, token).ConfigureAwait(false);
        foreach (var group in references)
        {
            if (group.Definition is not IMethodSymbol symbol || !IsSameMethod(Canonical(symbol), definition)) continue;
            foreach (var locations in group.Locations.Where(location => !location.IsImplicit
                && filesById.ContainsKey(location.Document.Id)).GroupBy(location => location.Document))
            {
                var callerRoot = await locations.Key.GetSyntaxRootAsync(token).ConfigureAwait(false);
                var callerModel = await locations.Key.GetSemanticModelAsync(token).ConfigureAwait(false);
                if (callerRoot is null || callerModel is null) continue;
                var file = filesById[locations.Key.Id];
                foreach (var location in locations)
                {
                    token.ThrowIfCancellationRequested();
                    var start = location.Location.SourceSpan.Start;
                    var call = callerRoot.FindToken(start).Parent?.AncestorsAndSelf()
                        .TakeWhile(node => node is ExpressionSyntax or SimpleNameSyntax or ConstructorInitializerSyntax)
                        .FirstOrDefault(node => callerModel.GetOperation(node, token) switch
                        {
                            IInvocationOperation invocation => IsSameMethod(Canonical(invocation.TargetMethod), definition),
                            IObjectCreationOperation { Constructor: { } constructor } =>
                                IsSameMethod(Canonical(constructor), definition),
                            _ => false
                        });
                    if (call is null) continue;
                    var owner = call.Ancestors().FirstOrDefault(node =>
                        node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
                    var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    result.Add(new CallerLocation(file, owner?.SpanStart, new SourceRange(call.SpanStart, call.Span.Length, line, file.Path)));
                    if (result.Count >= CallerLimit) return SortCallers(result, true);
                }
            }
        }
        return SortCallers(result, false);
    }

    private static IMethodSymbol Canonical(IMethodSymbol method) =>
        (method.ReducedFrom ?? method).OriginalDefinition;

    private static bool IsSameMethod(IMethodSymbol left, IMethodSymbol right) =>
        SymbolEqualityComparer.Default.Equals(left, right);

    private static CallerSearch SortCallers(IEnumerable<CallerLocation> locations, bool partial) => new(
        locations.Distinct().OrderBy(location => location.File.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(location => location.Source.Offset).ToArray(), partial);
}
