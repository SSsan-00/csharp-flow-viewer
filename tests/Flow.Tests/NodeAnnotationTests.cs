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
}
