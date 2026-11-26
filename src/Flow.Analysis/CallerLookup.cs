using System.Collections.Immutable;
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

    private const int CallerResultLimit = 64;
    private const int IndexedSiteLimit = 50_000;
    private readonly object callerCacheLock = new();
    private readonly Dictionary<(ProjectFile, int), CallerSearch> callerResults = [];
    private readonly Dictionary<ProjectFile, IReadOnlyDictionary<(ProjectFile, int), List<CallerLocation>>> jsCallerIndexes = [];
    private int indexedSiteCount;

    public async Task<CallerSearch> FindCallersAsync(ProjectFile targetFile, int targetId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = (targetFile, targetId);
        lock (callerCacheLock)
            if (callerResults.TryGetValue(key, out var cached)) return cached;

        if (!Files.Contains(targetFile)) return new CallerSearch([]);
        var result = targetFile.Id is { } documentId && project.GetDocument(documentId) is { } document
            ? await FindCSharpCallersAsync(document, targetId, token).ConfigureAwait(false)
            : await FindJavaScriptCallersAsync(targetFile, targetId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (callerCacheLock)
        {
            if (!result.IsPartial)
            {
                if (callerResults.Count >= CallerResultLimit) callerResults.Remove(callerResults.Keys.First());
                callerResults[key] = result;
            }
        }
        return result;
    }

    private async Task<CallerSearch> FindJavaScriptCallersAsync(ProjectFile targetFile, int targetId, CancellationToken token)
    {
        var locations = new Dictionary<(string Path, int Offset, int Length), CallerLocation>();
        var filesByPath = FilesByPath;
        var candidates = Files.Where(file => file.IsRazor || file.IsJavaScript)
            .Where(file => !targetFile.IsRazor || file == targetFile)
            .OrderByDescending(file => file == targetFile).Take(JavaScriptFileLimit + 1).ToArray();
        var partial = candidates.Length > JavaScriptFileLimit;
        var key = (targetFile, targetId);
        foreach (var file in candidates.Take(JavaScriptFileLimit))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var index = await ReadJavaScriptCallerIndexAsync(file, filesByPath, key, token).ConfigureAwait(false);
                if (!index.TryGetValue(key, out var matches)) continue;
                foreach (var location in matches)
                {
                    token.ThrowIfCancellationRequested();
                    locations.TryAdd((location.File.Path, location.Source.Offset, location.Source.Length), location);
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

    private async Task<IReadOnlyDictionary<(ProjectFile, int), List<CallerLocation>>> ReadJavaScriptCallerIndexAsync(
        ProjectFile file, IReadOnlyDictionary<string, ProjectFile> files, (ProjectFile, int) targetKey, CancellationToken token)
    {
        int budget;
        lock (callerCacheLock)
        {
            if (jsCallerIndexes.TryGetValue(file, out var cached)) return cached;
            budget = IndexedSiteLimit - indexedSiteCount;
        }

        // Browser call sites don't depend on Razor's server-side PageModel binding.
        var sites = file.IsRazor
            ? (await PageModelResolver.ReadAsync(project, file, Files, LocalScripts(), token, bindHandlers: false).ConfigureAwait(false)).CallSites(token)
            : (await ReadJavaScriptAsync(file, token, null, null, collectSourceBrackets: false).ConfigureAwait(false)).CallSites(token);
        var index = new Dictionary<(ProjectFile, int), List<CallerLocation>>();
        var count = 0;
        var cacheable = true;
        foreach (var site in sites)
        {
            token.ThrowIfCancellationRequested();
            if (site.Target is not { } target || !files.TryGetValue(target.FilePath, out var destination)
                || site.Source.FilePath is not { } path || !files.TryGetValue(path, out var owner)) continue;
            var key = (destination, target.Method);
            if (cacheable && count >= budget)
            {
                index.TryGetValue(targetKey, out var retained);
                index.Clear();
                if (retained is not null) index.Add(targetKey, retained);
                cacheable = false;
            }
            if (!cacheable && key != targetKey) continue;
            if (!index.TryGetValue(key, out var matches)) index.Add(key, matches = []);
            var entryId = owner.IsJavaScript ? site.Owner.Source.Offset : site.Owner.Id;
            matches.Add(new CallerLocation(owner, entryId, site.Source, file.IsRazor && owner != file ? file : null));
            count++;
        }

        token.ThrowIfCancellationRequested();
        lock (callerCacheLock)
        {
            if (cacheable && indexedSiteCount + count <= IndexedSiteLimit && !jsCallerIndexes.ContainsKey(file))
            {
                jsCallerIndexes.Add(file, index);
                indexedSiteCount += count;
            }
        }
        return index;
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
        var documents = project.Documents.Where(item => filesById.ContainsKey(item.Id)).ToImmutableHashSet();
        var references = await SymbolFinder.FindReferencesAsync(definition, project.Solution, documents, token).ConfigureAwait(false);
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
