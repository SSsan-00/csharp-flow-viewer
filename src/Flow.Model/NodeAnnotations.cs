namespace Flow.Model;

public sealed class NodeAnnotations
{
    public HashSet<string> Marked { get; init; } = [];
    public HashSet<string> GrayRoots { get; init; } = [];

    public HashSet<string> GrayNodes(FlowDiagram diagram)
    {
        var result = new HashSet<string>(GrayRoots);
        if (GrayRoots.Count == 0)
            return result;

        var reachableEdges = diagram.Edges.Where(edge => edge.IsReachable).ToArray();
        var incoming = reachableEdges.Select(edge => edge.To).ToHashSet();
        var outgoing = reachableEdges.ToLookup(edge => edge.From, edge => edge.To);
        var entries = diagram.Nodes.Where(node => node.IsReachable &&
            (node.Kind == NodeKind.Start || !incoming.Contains(node.Id))).Select(node => node.Id).ToArray();
        foreach (var root in GrayRoots)
        {
            var outside = Reachable(entries, root);
            result.UnionWith(Reachable([root]).Where(id => !outside.Contains(id)));
        }
        return result;

        HashSet<string> Reachable(IEnumerable<string> starts, string? skip = null)
        {
            var seen = new HashSet<string>();
            var queue = new Queue<string>(starts);
            while (queue.TryDequeue(out var id))
            {
                if (id == skip || !seen.Add(id)) continue;
                foreach (var next in outgoing[id])
                    queue.Enqueue(next);
            }
            return seen;
        }
    }
}
