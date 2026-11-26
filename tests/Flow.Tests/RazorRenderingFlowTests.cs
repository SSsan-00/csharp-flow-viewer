using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorRenderingFlowTests
{
    [TestMethod]
    public void ConsecutiveMarkupIsGroupedWithinItsOwnRazorBranchInSourceOrder()
    {
        var source = """
            @if (Model.Ready) {
                <text><span>first</span></text>
                @: <b>second</b>
                @if (Model.Special) { <i>special</i> }
                @: <u>last</u>
            } else {
                <p>other</p>
            }
            """;
        var page = new RazorSourceFile(source, Path.GetFullPath("Display.cshtml"));
        var graph = page.Analyze(-1);
        var ready = Condition(graph, "Model.Ready");
        var before = Next(graph, Next(graph, ready, "True"));
        StringAssert.Contains(before.Label, "<span>first</span> <b>second</b>");
        Assert.AreEqual(2, before.Source!.Line);
        var special = Next(graph, before);
        Assert.AreEqual(Condition(graph, "Model.Special"), special);
        StringAssert.Contains(Next(graph, special, "True").Label, "<i>special</i>");
        var last = Next(graph, Next(graph, special, "False"));
        StringAssert.Contains(last.Label, "<u>last</u>");
        Assert.AreEqual(5, last.Source!.Line);
        StringAssert.Contains(Next(graph, ready, "False").Label, "<p>other</p>");
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void MarkupOnlyIfAndElseIfShowBothOutcomesAndJoin(string newline)
    {
        var source = """
            @page
            @if (Model.IsReady)
            {
                <p>Ready</p>
            }
            else if (Model.IsPending)
            {
                <p>Pending</p>
            }
            else
            {
                <p>Unavailable</p>
            }
            """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        var path = Path.GetFullPath("Conditional.cshtml");
        var page = new RazorSourceFile(source, path);
        Assert.AreEqual(1, page.Entries.Count);
        var graph = page.Analyze(-1);

        var first = Condition(graph, "Model.IsReady");
        var second = Condition(graph, "Model.IsPending");
        Assert.IsNotNull(first.Source);
        Assert.AreEqual(path, first.Source.FilePath);
        Assert.AreEqual(2, first.Source.Line);
        Assert.AreEqual("Model.IsReady", source.Substring(first.Source.Offset, first.Source.Length));

        var whenTrue = Next(graph, first, "True");
        var whenFalse = Next(graph, first, "False");
        Assert.AreEqual(second, Next(graph, whenFalse));
        var join = Next(graph, whenTrue);
        Assert.AreEqual("合流", join.Label);
        Assert.AreEqual(join, Next(graph, Next(graph, Next(graph, second, "True"))));
        Assert.AreEqual(join, Next(graph, Next(graph, Next(graph, second, "False"))));
    }

    [TestMethod]
    public void ForAndNestedIfShowBreakContinueAndFalseExit()
    {
        var page = new RazorSourceFile("""
            @page
            @for (var index = 0; index < Model.Total; index++)
            {
                if (Model.Skip(index))
                {
                    continue;
                }
                if (Model.Stop(index))
                {
                    break;
                }
                <p>@index</p>
            }
            """, Path.GetFullPath("Loop.cshtml"));
        var graph = page.Analyze(-1);

        var loop = Condition(graph, "index < Model.Total");
        var skip = Condition(graph, "Model.Skip(index)");
        var stop = Condition(graph, "Model.Stop(index)");
        var end = Next(graph, loop, "False");
        Assert.AreEqual(NodeKind.End, end.Kind);
        Assert.AreEqual(skip, Next(graph, Next(graph, loop, "True")));
        Assert.AreEqual(stop, Next(graph, Next(graph, skip, "False")));
        Assert.AreEqual(loop, Next(graph, Next(graph, Next(graph, skip, "True"))));
        Assert.AreEqual(end, Next(graph, Next(graph, Next(graph, stop, "True"))));
        var drawn = Next(graph, Next(graph, stop, "False"));
        StringAssert.Contains(drawn.Label, "<p>");
        Assert.AreEqual(loop, Next(graph, drawn));
        Assert.DoesNotContain(node => node.Label == "更新して次の反復へ", graph.Nodes);
    }

    [TestMethod]
    public void SwitchCasesAndBrowserEventsRemainSeparate()
    {
        var source = """
            @page
            @switch (Model.State)
            {
                case 1:
                    <button id="first" type="button" onclick="go()">One</button>
                    break;
                default:
                    <p>Other</p>
                    break;
            }
            <script>function go() { if (window.ready) return 1; return 0; }</script>
            """;
        var page = new RazorSourceFile(source, Path.GetFullPath("Switch.cshtml"));
        var graph = page.Analyze(-1);
        var choice = Condition(graph, "switch (Model.State)");
        var first = Next(graph, choice, "case 1");
        var fallback = Next(graph, choice, "default");
        var uiEvent = Next(graph, first, "この描画経路のイベント候補");
        Assert.IsNotNull(uiEvent.TargetMethod);
        StringAssert.Contains(uiEvent.Label, "#first");
        Assert.AreEqual("break", Next(graph, first).Label);
        Assert.AreEqual("break", Next(graph, fallback).Label);
        Assert.DoesNotContain(node => node.Label.Contains("window.ready", StringComparison.Ordinal), graph.Nodes);
        Assert.Contains(node => node.Label.Contains("window.ready", StringComparison.Ordinal), page.Analyze(page.Entries.Single(entry => entry.Name == "go").Id).Nodes);
    }

    [TestMethod]
    public void ForeachAndWhileEachShowAnExitAndBackEdge()
    {
        var page = new RazorSourceFile("""
            @page
            @foreach (var item in Model.Items)
            {
                <p>@item</p>
            }
            @while (Model.More())
            {
                <p>More</p>
            }
            """, Path.GetFullPath("Loops.cshtml"));
        var graph = page.Analyze(-1);
        var items = Condition(graph, "Model.Items");
        var more = Condition(graph, "Model.More()");
        Assert.AreEqual(more, Next(graph, items, "要素なし"));
        Assert.AreEqual(items, Next(graph, Next(graph, Next(graph, items, "要素あり"))));
        Assert.AreEqual(more, Next(graph, Next(graph, Next(graph, more, "True"))));
        Assert.AreEqual(NodeKind.End, Next(graph, more, "False").Kind);
    }

    [TestMethod]
    public void EmptyIfWithoutElseJoinsAndGotoDoesNotPretendToFallThrough()
    {
        var page = new RazorSourceFile("""
            @page
            @if (Model.Visible)
            {
            }
            @switch (Model.State)
            {
                case 1:
                    goto case 2;
                case 2:
                    <p>Two</p>
                    break;
            }
            """, Path.GetFullPath("EmptyAndGoto.cshtml"));
        var graph = page.Analyze(-1);
        var check = Condition(graph, "Model.Visible");
        var choice = Condition(graph, "switch (Model.State)");
        var join = Next(graph, check, "False");
        Assert.AreEqual(join, Next(graph, Next(graph, check, "True")));
        Assert.AreEqual(choice, Next(graph, join));
        var boundary = Next(graph, Next(graph, choice, "case 1"));
        StringAssert.Contains(boundary.Label, "goto");
        Assert.DoesNotContain(edge => edge.From == boundary.Id, graph.Edges);
        Assert.AreEqual(NodeKind.End, Next(graph, choice, "一致なし").Kind);
    }

    private static FlowNode Condition(FlowDiagram graph, string text) =>
        graph.Nodes.Single(node => node.Kind == NodeKind.Condition && node.Label.Contains(text, StringComparison.Ordinal));

    private static FlowNode Next(FlowDiagram graph, FlowNode node, string? label = null) =>
        graph.Nodes.Single(target => target.Id == graph.Edges.Single(edge => edge.From == node.Id && edge.Label == label).To);
}
