using Flow.Model;

namespace Flow.Desktop;

internal static class FlowCacheWeight
{
    public const int Capacity = 16;
    public const long Budget = 24L * 1024 * 1024;

    // A retention budget, not a managed-heap measurement; source objects have a separate lifecycle.
    public static long Estimate(FlowDiagram graph, string? mermaid)
    {
        long weight = 256 + Text(mermaid);
        foreach (var node in graph.Nodes)
        {
            weight += 512 + Text(node.Id) + Text(node.Label)
                + Text(node.TargetFilePath) + Text(node.Source?.FilePath) + Text(node.TargetDeclaration?.FilePath);
            if (node.ImplementationCandidates is { } implementations)
                foreach (var candidate in implementations)
                    weight += 256 + Text(candidate.Label) + Text(candidate.FilePath) + Text(candidate.Declaration.FilePath);
            if (node.CallTargets is { } targets)
                foreach (var target in targets)
                    weight += 128 + Text(target.Label) + Text(target.FilePath);
        }
        foreach (var edge in graph.Edges) weight += 128 + Text(edge.From) + Text(edge.To) + Text(edge.Label);
        foreach (var notice in graph.Notices) weight += 32 + Text(notice);
        return weight;
    }

    private static long Text(string? text) => 2L * (text?.Length ?? 0);
}
