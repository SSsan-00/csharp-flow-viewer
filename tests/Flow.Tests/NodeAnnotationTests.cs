using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class NodeAnnotationTests
{
    [TestMethod]
    public void GrayStopsAtSharedMergeEvenWithABackEdge()
    {
        var nodes = new[] { "start", "loop", "save", "notify", "reject", "audit", "end" }
            .Select(id => new FlowNode(id, id == "start" ? NodeKind.Start : NodeKind.Call, id, null)).ToArray();
        var diagram = new FlowDiagram(nodes,
        [
            new("start", "loop"), new("loop", "save"), new("loop", "reject"),
            new("save", "notify"), new("notify", "audit"), new("reject", "audit"),
            new("audit", "loop"), new("audit", "end")
        ], []);
        var annotations = new NodeAnnotations { GrayRoots = ["save"], Marked = ["audit"] };
        CollectionAssert.AreEquivalent(new[] { "save", "notify" }, annotations.GrayNodes(diagram).ToArray());

        annotations.GrayRoots.Remove("save");
        Assert.AreEqual(0, annotations.GrayNodes(diagram).Count);
        CollectionAssert.AreEquivalent(new[] { "audit" }, annotations.Marked.ToArray());
    }
    [TestMethod]
    public void DominatorBasedGrayMatchesOriginalRuleOnCyclicAndDisconnectedGraphs()
    {
        var random = new Random(327);
        for (var sample = 0; sample < 700; ++sample)
        {
            var nodes = Enumerable.Range(0, 16).Select(i => new FlowNode(
                "n" + i, i == 0 || (i == 5 && sample % 2 == 0) ? NodeKind.Start : NodeKind.Call,
                "", null, IsReachable: i == 0 || random.Next(5) != 0, IsCaseTest: random.Next(4) == 0)).ToArray();
            var edges = Enumerable.Range(0, random.Next(10, 55)).Select(_ => new FlowEdge(
                "n" + random.Next(nodes.Length), "n" + random.Next(nodes.Length), random.Next(2) == 0 ? "True" : "False",
                IsReachable: random.Next(4) != 0)).ToArray();
            var graph = new FlowDiagram(nodes, edges, []);
            var roots = nodes.Where(_ => random.Next(3) == 0).Select(node => node.Id).ToHashSet();
            roots.Add("missing");
            var actual = new NodeAnnotations { GrayRoots = roots }.GrayNodes(graph);
            CollectionAssert.AreEquivalent(ReferenceGray(graph, roots).ToArray(), actual.ToArray(), $"seed sample {sample}");
        }
    }

    [TestMethod]
    public void ManyRootsOnLargeGraphColorOnlyTheirDominatedNodes()
    {
        const int size = 32_002;
        var nodes = Enumerable.Range(0, size).Select(i => new FlowNode(
            "n" + i, i == 0 ? NodeKind.Start : NodeKind.Call, "", null)).ToArray();
        var edges = Enumerable.Range(1, size - 1).Select(i => new FlowEdge(nodes[i - 1].Id, nodes[i].Id)).ToArray();
        var gray = new NodeAnnotations
        {
            GrayRoots = Enumerable.Range(1, 1000).Select(i => nodes[i * 16].Id).ToHashSet()
        }.GrayNodes(new FlowDiagram(nodes, edges, []));
        Assert.AreEqual(size - 16, gray.Count);
        Assert.IsFalse(gray.Contains("n15"));
        Assert.IsTrue(gray.Contains("n16"));
        Assert.IsTrue(gray.Contains(nodes[^1].Id));
    }

    private static HashSet<string> ReferenceGray(FlowDiagram graph, HashSet<string> roots)
    {
        var result = new HashSet<string>(roots);
        var edges = graph.Edges.Where(edge => edge.IsReachable).ToArray();
        var incoming = edges.Select(edge => edge.To).ToHashSet();
        var outgoing = edges.ToLookup(edge => edge.From);
        var entries = graph.Nodes.Where(node => node.IsReachable &&
            (node.Kind == NodeKind.Start || !incoming.Contains(node.Id))).Select(node => node.Id).ToArray();
        foreach (var root in roots)
        {
            var isCase = graph.Nodes.Any(node => node.Id == root && node.IsCaseTest);
            var inside = Walk(isCase ? outgoing[root].Where(edge => edge.Label == "True").Select(edge => edge.To) : [root]);
            result.UnionWith(inside.Except(Walk(entries, root, isCase)));
        }
        return result;

        HashSet<string> Walk(IEnumerable<string> seeds, string? skip = null, bool matchOnly = false)
        {
            var seen = new HashSet<string>();
            var pending = new Queue<string>(seeds);
            while (pending.TryDequeue(out var id))
            {
                if ((id == skip && !matchOnly) || !seen.Add(id)) continue;
                foreach (var edge in outgoing[id])
                    if (!(matchOnly && id == skip && edge.Label == "True")) pending.Enqueue(edge.To);
            }
            return seen;
        }
    }
}
