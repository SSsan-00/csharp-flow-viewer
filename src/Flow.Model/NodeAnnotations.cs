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

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var outgoing = new List<List<int>> { new() };
        var incoming = new List<List<int>> { new() };
        var caseBranches = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in diagram.Nodes)
        {
            Id(node.Id);
            if (node.IsCaseTest)
                caseBranches[node.Id] = AddVertex();
        }
        var originalDestinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in diagram.Edges)
        {
            if (!edge.IsReachable) continue;
            var from = Id(edge.From);
            if (edge.Label == "True" && caseBranches.TryGetValue(edge.From, out var branch))
            {
                if (incoming[branch].Count == 0) AddEdge(from, branch);
                from = branch;
            }
            AddEdge(from, Id(edge.To));
            originalDestinations.Add(edge.To);
        }
        foreach (var node in diagram.Nodes)
            if (node.IsReachable && (node.Kind == NodeKind.Start || !originalDestinations.Contains(node.Id)))
                AddEdge(0, Id(node.Id));

        // A case cuts only its matching edges. Splitting those edges through one
        // synthetic vertex makes that cut equivalent to removing a regular vertex.
        var roots = GrayRoots.Where(ids.ContainsKey).Select(root => caseBranches.GetValueOrDefault(root, ids[root])).ToArray();
        var rpo = ReversePostorder(outgoing);
        var rank = Enumerable.Repeat(-1, outgoing.Count).ToArray();
        for (var i = 0; i < rpo.Count; ++i) rank[rpo[i]] = i;
        var dominators = ImmediateDominators(rpo, rank, incoming);
        var gray = new bool[outgoing.Count];
        foreach (var root in roots) gray[root] = true;
        foreach (var vertex in rpo)
            gray[vertex] |= gray[dominators[vertex]];

        // The old rule also colors disconnected nodes reached from a marked root.
        // None is reachable from the entry, so a single multi-source walk suffices.
        if (rank.Contains(-1))
        {
            var seen = new bool[outgoing.Count];
            var queue = new Queue<int>(roots);
            while (queue.TryDequeue(out var vertex))
            {
                if (seen[vertex]) continue;
                seen[vertex] = true;
                if (rank[vertex] < 0) gray[vertex] = true;
                foreach (var next in outgoing[vertex]) queue.Enqueue(next);
            }
        }

        foreach (var (id, vertex) in ids)
            if (gray[vertex]) result.Add(id);
        return result;

        int Id(string id)
        {
            if (!ids.TryGetValue(id, out var vertex)) ids.Add(id, vertex = AddVertex());
            return vertex;
        }

        int AddVertex()
        {
            outgoing.Add([]);
            incoming.Add([]);
            return outgoing.Count - 1;
        }

        void AddEdge(int from, int to)
        {
            outgoing[from].Add(to);
            incoming[to].Add(from);
        }
    }

    private static List<int> ReversePostorder(IReadOnlyList<List<int>> outgoing)
    {
        var seen = new bool[outgoing.Count];
        var postorder = new List<int>(outgoing.Count);
        var stack = new Stack<(int Vertex, int Next)>();
        seen[0] = true;
        stack.Push((0, 0));
        while (stack.TryPop(out var frame))
        {
            var neighbors = outgoing[frame.Vertex];
            if (frame.Next == neighbors.Count)
            {
                postorder.Add(frame.Vertex);
                continue;
            }
            stack.Push((frame.Vertex, frame.Next + 1));
            var next = neighbors[frame.Next];
            if (seen[next]) continue;
            seen[next] = true;
            stack.Push((next, 0));
        }
        postorder.Reverse();
        return postorder;
    }

    private static int[] ImmediateDominators(IReadOnlyList<int> rpo, int[] rank, IReadOnlyList<List<int>> incoming)
    {
        var dominator = Enumerable.Repeat(-1, incoming.Count).ToArray();
        dominator[0] = 0;
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 1; i < rpo.Count; ++i)
            {
                var vertex = rpo[i];
                var parent = -1;
                foreach (var predecessor in incoming[vertex])
                {
                    if (dominator[predecessor] < 0) continue;
                    parent = parent < 0 ? predecessor : Intersect(parent, predecessor);
                }
                if (dominator[vertex] == parent) continue;
                dominator[vertex] = parent;
                changed = true;
            }
        }
        return dominator;

        int Intersect(int left, int right)
        {
            while (left != right)
            {
                while (rank[left] > rank[right]) left = dominator[left];
                while (rank[right] > rank[left]) right = dominator[right];
            }
            return left;
        }
    }
}
