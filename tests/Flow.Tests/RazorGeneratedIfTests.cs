using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorGeneratedIfTests
{
    [TestMethod]
    [DataRow(false, false, "\n")]
    [DataRow(true, false, "\r\n")]
    [DataRow(true, true, "\r\n")]
    public void GeneratedIfShowsAStaticClientConditionNextToRazorData(bool ready, bool nested, string newline)
    {
        var entryStart = ready ? "$(function () {" : "function run() {";
        var entryEnd = ready ? "});" : "}";
        var outerStart = nested ? "@if (Model.HasPage) { <text>" : "";
        var outerEnd = nested ? "</text> }" : "";
        var source = $$"""
            <script>
            function apply() {}
            function revert() {}
            {{entryStart}}
                {{outerStart}}
                @if (Model.ShowActions) {
                    <text>
                        const label = "@(Model.Title)";
                        if (clientReady) { apply(); } else { revert(); }
                    </text>
                } else {
                    <text>cancel();</text>
                }
                {{outerEnd}}
            {{entryEnd}}
            </script>
            """.ReplaceLineEndings(newline);
        var path = Path.GetFullPath("GeneratedIf.cshtml");
        var page = new RazorSourceFile(source, path);
        var graph = page.Analyze(page.Entries.Single(entry =>
            entry.Name.StartsWith(ready ? "JavaScript " : "run", StringComparison.Ordinal)).Id);
        var server = Assert.ContainsSingle(graph.Nodes.Where(node => node.Kind == NodeKind.Condition &&
            node.Source is { } span && source.Substring(span.Offset, span.Length) == "Model.ShowActions"));
        if (nested)
        {
            var outer = Assert.ContainsSingle(graph.Nodes.Where(node => node.Label == "Model.HasPage"));
            Assert.AreEqual(server, Next(graph, outer, "True"));
        }
        var rendered = Next(graph, server, "True");
        var condition = Next(graph, rendered, "生成JS（候補）");
        Assert.AreEqual((NodeKind.Condition, "clientReady"), (condition.Kind, condition.Label));
        Assert.AreEqual(source.IndexOf("clientReady", StringComparison.Ordinal), condition.Source!.Offset);
        foreach (var (outcome, name) in new[] { ("True", "apply"), ("False", "revert") })
        {
            var call = Next(graph, condition, outcome);
            Assert.AreEqual(name + "()", call.Label);
            Assert.AreEqual(source.IndexOf(name + "();", StringComparison.Ordinal), call.Source!.Offset);
            Assert.AreEqual(page.Entries.Single(entry => entry.Name == name).Id, call.TargetMethod);
            Assert.AreEqual(path, call.TargetFilePath);
        }
        Assert.AreEqual("cancel()", Next(graph, Next(graph, server, "False"), "生成JS（候補）").Label);
    }

    [TestMethod]
    [DataRow("if (@(Model.Enabled)) { apply(); }")]
    [DataRow("@Html.Raw(Model.Script); if (clientReady) { apply(); }")]
    [DataRow("if (clientReady) { apply();")]
    public void UnknownCodeOrIncompleteConditionStaysAPreview(string generated)
    {
        var page = new RazorSourceFile($$"""
            <script>
            function apply() {}
            function run() {
                @if (Model.ShowActions) {
                    <text>{{generated}}</text>
                }
            }
            </script>
            """, Path.GetFullPath("UnknownIf.cshtml"));
        var graph = page.Analyze(page.Entries.Single(entry => entry.Name == "run").Id);
        var server = Assert.ContainsSingle(graph.Nodes.Where(node => node.Label == "Model.ShowActions"));
        var rendered = Next(graph, server, "True");
        StringAssert.Contains(rendered.Label, "生成JS");
        Assert.DoesNotContain(edge => edge.From == rendered.Id && edge.Label == "生成JS（候補）", graph.Edges);
        Assert.DoesNotContain(node => node.Label is "apply()" or "clientReady" or "null", graph.Nodes);
        Assert.IsTrue(graph.IsPartial);
    }

    private static FlowNode Next(FlowDiagram graph, FlowNode from, string? outcome)
    {
        var edge = Assert.ContainsSingle(graph.Edges.Where(edge => edge.From == from.Id && edge.Label == outcome));
        return graph.Nodes.Single(node => node.Id == edge.To);
    }
}