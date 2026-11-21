using Flow.Desktop;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class DesktopFlowCacheTests
{
    [TestMethod]
    public void LargeFlowCanBeReusedAcrossTheRoundTrip()
    {
        // Same scale as the documented 8,002-node/12,001-edge large-method case.
        var nodes = Enumerable.Range(0, 8_002).Select(i => new FlowNode(
            $"n{i}", NodeKind.Condition, "Order_has_been_validated_and_the_default_dispatch_route_can_accept_the_request",
            new SourceRange(i, 20, i + 1))).ToArray();
        var edges = Enumerable.Range(0, 12_001).Select(i => new FlowEdge(
            nodes[i % nodes.Length].Id, nodes[(i + 1) % nodes.Length].Id)).ToArray();
        var graph = new FlowDiagram(nodes, edges, []);
        var weight = FlowCacheWeight.Estimate(graph, new string('x', 690_000));
        Assert.IsGreaterThan(0L, weight);
        Assert.IsLessThan(FlowCacheWeight.Budget, weight);
        var cache = new BoundedCache<string, FlowDiagram>(FlowCacheWeight.Capacity, FlowCacheWeight.Budget);

        cache.Add("A", graph, weight);
        cache.Add("B", new FlowDiagram([], [], []), 256);

        Assert.IsTrue(cache.TryGetValue("A", out var revisited));
        Assert.AreSame(graph, revisited);
    }

    [TestMethod]
    public void BudgetAndCapacityEvictLeastRecentlyUsedAndRejectOversizedEntry()
    {
        var cache = new BoundedCache<string, string>(2, 10);
        cache.Add("old", "1", 4);
        cache.Add("recent", "2", 4);
        Assert.IsTrue(cache.TryGetValue("old", out _));

        cache.Add("large", "3", 6);
        Assert.IsTrue(cache.TryGetValue("large", out _));
        Assert.IsTrue(cache.TryGetValue("old", out _));
        Assert.IsFalse(cache.TryGetValue("recent", out _));

        cache.Add("too-large", "4", 11);
        Assert.IsFalse(cache.TryGetValue("too-large", out _));
        Assert.IsTrue(cache.TryGetValue("large", out _));

        cache.Clear();
        cache.Add("one", "1", 1);
        cache.Add("two", "2", 1);
        cache.Add("three", "3", 1);
        Assert.IsFalse(cache.TryGetValue("one", out _));
        Assert.IsTrue(cache.TryGetValue("two", out _));
        Assert.IsTrue(cache.TryGetValue("three", out _));
    }

    [TestMethod]
    public void SourceEvictionAndClearReleaseBudget()
    {
        var cache = new BoundedCache<(string Source, int Id), string>(16, 12);
        cache.Add(("first", 1), "A", 3);
        cache.Add(("second", 1), "B", 3);
        cache.Add(("first", 2), "C", 3);
        cache.RemoveWhere(key => key.Source == "first");
        cache.Add(("second", 2), "D", 9);

        Assert.IsFalse(cache.TryGetValue(("first", 1), out _));
        Assert.IsFalse(cache.TryGetValue(("first", 2), out _));
        Assert.IsTrue(cache.TryGetValue(("second", 1), out _));
        Assert.IsTrue(cache.TryGetValue(("second", 2), out _));

        cache.Clear();
        cache.Add(("full", 1), "E", 12);
        Assert.IsFalse(cache.TryGetValue(("second", 1), out _));
        Assert.IsTrue(cache.TryGetValue(("full", 1), out _));
    }
}
