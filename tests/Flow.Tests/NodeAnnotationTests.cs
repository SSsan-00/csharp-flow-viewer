using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class NodeAnnotationTests
{
    [TestMethod]
    public void MarkColorsAllMandatoryPredecessorsButNeitherOptionalBranchNorAForwardLoop()
    {
        var graph = Flow([
            ("start", "prepare"), ("prepare", "if"), ("if", "yes"), ("if", "no"),
            ("yes", "merge"), ("no", "merge"), ("merge", "target"), ("target", "late"), ("late", "prepare")
        ]);
        var annotations = new NodeAnnotations { Marked = ["target"] };
        CollectionAssert.AreEquivalent(new[] { "start", "prepare", "if", "merge", "target" },
            annotations.VisibleMarks(graph).ToArray());
        Assert.IsTrue(annotations.Marked.Add("yes"));
        CollectionAssert.AreEquivalent(new[] { "start", "prepare", "if", "yes", "merge", "target" },
            annotations.VisibleMarks(graph).ToArray());
        Assert.IsTrue(annotations.Marked.Remove("target"));
        CollectionAssert.AreEquivalent(new[] { "start", "prepare", "if", "yes" },
            annotations.VisibleMarks(graph).ToArray());

        var restored = System.Text.Json.JsonSerializer.Deserialize<NodeAnnotations>(
            System.Text.Json.JsonSerializer.Serialize(annotations))!;
        CollectionAssert.AreEquivalent(new[] { "yes" }, restored.Marked.ToArray());
        CollectionAssert.AreEquivalent(annotations.VisibleMarks(graph).ToArray(), restored.VisibleMarks(graph).ToArray());
        restored.Marked.Clear();
        Assert.IsEmpty(restored.VisibleMarks(graph));
    }

    [TestMethod]
    public void IndependentEntriesAndUnreachableNodesDoNotImplyOneRoute()
    {
        var graph = new FlowDiagram(
        [
            new("page", NodeKind.Start, "", null),
            new("render", NodeKind.Start, "", null),
            new("request", NodeKind.Exception, "", null, IsIndependentEntry: true),
            new("browser", NodeKind.Start, "", null),
            new("output", NodeKind.Call, "", null),
            new("detached", NodeKind.Call, "", null),
            new("notReached", NodeKind.Call, "", null, IsReachable: false),
            new("terminal", NodeKind.End, "", null),
            new("separate", NodeKind.Call, "", null)
        ],
        [
            new("page", "render"), new("render", "output"), new("output", "terminal"),
            new("request", "terminal"), new("browser", "terminal"), new("browser", "separate"),
            new("browser", "output", "False", IsReachable: false), new("browser", "notReached"), new("notReached", "output"),
            new("detached", "output")
        ], []);
        AssertMarks(graph, ["terminal"], "terminal");
        AssertMarks(graph, ["output"], "page", "render", "output");
        AssertMarks(graph, ["separate"], "browser", "separate");
        AssertMarks(graph, ["detached", "notReached"], "detached", "notReached");
    }

    [TestMethod]
    public void MarkingLargeCyclicGraphsWithManyTargetsDoesNotEnumerateRoutes()
    {
        const int count = 32_002;
        var nodes = Enumerable.Range(0, count).Select(i => new FlowNode(
            "n" + i, i == 0 ? NodeKind.Start : NodeKind.Call, "", null)).ToArray();
        var edges = Enumerable.Range(1, count - 1).Select(i => new FlowEdge(nodes[i - 1].Id, nodes[i].Id))
            .Append(new FlowEdge(nodes[^1].Id, nodes[1].Id)).ToArray();
        var graph = new FlowDiagram(nodes, edges, []);
        var annotations = new NodeAnnotations { Marked = Enumerable.Range(1, 1000).Select(i => nodes[i * 31].Id).ToHashSet() };
        Assert.HasCount(31_001, annotations.VisibleMarks(graph));
        annotations.Marked.Remove(nodes[31_000].Id);
        Assert.HasCount(30_970, annotations.VisibleMarks(graph));
    }

    [TestMethod]
    public void VisibleMarksMatchRemovingEachPredecessorOnCyclicGraphs()
    {
        var random = new Random(841);
        for (var sample = 0; sample < 150; ++sample)
        {
            var nodes = Enumerable.Range(0, 12).Select(i => new FlowNode("n" + i,
                i == 0 ? NodeKind.Start : NodeKind.Call, "", null, IsReachable: i <= 1 || random.Next(5) > 0,
                IsIndependentEntry: i == 1 && sample % 2 == 0)).ToArray();
            var edges = Enumerable.Range(0, random.Next(10, 45)).Select(_ => new FlowEdge(
                nodes[random.Next(nodes.Length)].Id, nodes[random.Next(1, nodes.Length)].Id,
                IsReachable: random.Next(5) > 0)).ToArray();
            var targets = nodes.Where(_ => random.Next(3) == 0).Select(node => node.Id).ToHashSet();
            var reachable = nodes.Where(node => node.IsReachable).Select(node => node.Id).ToHashSet();
            var next = edges.Where(edge => edge.IsReachable && reachable.Contains(edge.From) && reachable.Contains(edge.To))
                .ToLookup(edge => edge.From, edge => edge.To);
            var entries = nodes.Where(node => node.Kind == NodeKind.Start || node.IsIndependentEntry)
                .Select(node => node.Id).ToArray();
            var reachedTargets = ReachableWithout().Intersect(targets).ToArray();
            var expected = targets.Concat(reachable.Where(id => reachedTargets.Except(ReachableWithout(id)).Any())).ToHashSet();
            CollectionAssert.AreEquivalent(expected.ToArray(),
                new NodeAnnotations { Marked = targets }.VisibleMarks(new FlowDiagram(nodes, edges, [])).ToArray(),
                $"seed sample {sample}");

            HashSet<string> ReachableWithout(string? skip = null)
            {
                var seen = new HashSet<string>();
                var pending = new Queue<string>(entries);
                while (pending.TryDequeue(out var id))
                {
                    if (id == skip || !seen.Add(id)) continue;
                    foreach (var to in next[id]) pending.Enqueue(to);
                }
                return seen;
            }
        }
    }

    private static FlowDiagram Flow((string From, string To)[] edges) => new(
        edges.SelectMany(edge => new[] { edge.From, edge.To }).Distinct()
            .Select(id => new FlowNode(id, id == "start" ? NodeKind.Start : NodeKind.Call, id, null)).ToArray(),
        edges.Select(edge => new FlowEdge(edge.From, edge.To)).ToArray(), []);

    private static void AssertMarks(FlowDiagram graph, HashSet<string> marks, params string[] expected)
    {
        var annotations = new NodeAnnotations { Marked = marks };
        CollectionAssert.AreEquivalent(expected, annotations.VisibleMarks(graph).ToArray());
    }

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
