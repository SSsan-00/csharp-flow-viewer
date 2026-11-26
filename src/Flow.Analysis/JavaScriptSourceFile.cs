using Flow.Model;

namespace Flow.Analysis;

/// <summary>A project JavaScript document, optionally sharing the originating Razor page's script context.</summary>
public sealed class JavaScriptSourceFile
{
    private readonly RazorJavaScript analyzer;
    private readonly Func<int, CancellationToken, FlowDiagram>? projectFlow;
    private readonly Dictionary<int, int> analysisIds;

    internal JavaScriptSourceFile(string path, string text, RazorJavaScript analyzer,
        Func<int, CancellationToken, FlowDiagram>? projectFlow)
    {
        FilePath = path;
        Text = text;
        this.analyzer = analyzer;
        this.projectFlow = projectFlow;
        var entries = analyzer.Entries.Where(entry => LocalJavaScriptFiles.PathComparer.Equals(entry.Source.FilePath, path))
            .DistinctBy(entry => entry.Source.Offset).ToArray();
        analysisIds = entries.ToDictionary(entry => entry.Source.Offset, entry => entry.Id);
        Entries = entries.Select(entry => entry with { Id = entry.Source.Offset }).ToArray();
    }

    public string FilePath { get; }
    public object NavigationContext => analyzer;
    public string Text { get; }
    public IReadOnlyList<SourceBracket> Brackets => analyzer.GetBrackets(FilePath);
    public IReadOnlyList<MethodEntry> Entries { get; }
    public IReadOnlyList<string> Notices => analyzer.Notices;

    public FlowDiagram Analyze(int entryId, CancellationToken token = default)
    {
        var analysisId = analysisIds[entryId];
        var flow = projectFlow is null ? analyzer.Analyze(analysisId, token) : projectFlow(analysisId, token);
        return flow with { Notices = flow.Notices.Concat(Notices).Distinct(StringComparer.Ordinal).ToArray() };
    }

    internal IEnumerable<RazorJavaScript.CallSite> CallSites(CancellationToken token) => analyzer.CallSites(null, token);

    internal JavaScriptSourceFile? OpenScript(string path) => analyzer.OpenScript(path, projectFlow);

    internal static JavaScriptSourceFile Open(RazorJavaScriptFragment fragment, LocalJavaScriptFiles local,
        CancellationToken token, bool collectSourceBrackets)
    {
        var path = fragment.Source.FilePath!;
        var analyzer = new RazorJavaScript(fragment.Text, path, [fragment], (specifier, origin) =>
            local.ResolveImport(specifier, origin, "", null, false) is { } dependency
                ? local.Read(dependency, module: true, token) : null, token, standalone: true,
            collectSourceBrackets: collectSourceBrackets);
        return analyzer.OpenScript(path)!;
    }
}
