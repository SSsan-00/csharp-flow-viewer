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
        var outgoing = reachableEdges.ToLookup(edge => edge.From);
        var caseTests = diagram.Nodes.Where(node => node.IsCaseTest).Select(node => node.Id).ToHashSet();
        var entries = diagram.Nodes.Where(node => node.IsReachable &&
            (node.Kind == NodeKind.Start || !incoming.Contains(node.Id))).Select(node => node.Id).ToArray();
        foreach (var root in GrayRoots)
        {
            var isCase = caseTests.Contains(root);
            var inside = isCase
                ? Reachable(outgoing[root].Where(edge => edge.Label == "True").Select(edge => edge.To))
                : Reachable([root]);
            var outside = Reachable(entries, root, cutMatchOnly: isCase);
            result.UnionWith(inside.Where(id => !outside.Contains(id)));
        }
        return result;

        HashSet<string> Reachable(IEnumerable<string> starts, string? skip = null, bool cutMatchOnly = false)
        {
            var seen = new HashSet<string>();
            var queue = new Queue<string>(starts);
            while (queue.TryDequeue(out var id))
            {
                if ((id == skip && !cutMatchOnly) || !seen.Add(id)) continue;
                foreach (var edge in outgoing[id])
                    if (!(cutMatchOnly && id == skip && edge.Label == "True"))
                        queue.Enqueue(edge.To);
            }
            return seen;
        }
    }
}
