using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorGeneratedLoopTests
{
    private const string Execute = "生成後、このJSが実行される場合";

    [TestMethod]
    [DataRow(false, "\n")]
    [DataRow(true, "\r\n")]
    public void ReadyLoopShowsBranchCallsAndAnOpaqueOutputSeparately(bool razorLines, string newline)
    {
        string[] statements = [
            "prepare();",
            "if (enabled) { apply(); } else { skip(); }",
            "@Html.Raw(Model.Fragment);",
            "finish();"
        ];
        var emitted = razorLines ? string.Join("\n", statements.Select(line => "@:" + line))
            : "<text>\n" + string.Join("\n", statements) + "\n</text>";
        var page = Page(emitted, newline);
        var graph = Ready(page);
        var loop = graph.Nodes.Single(node => node.Label == "描画条件: for (i < 3)");
        var render = Next(graph, loop, "True");
        Assert.AreEqual("ループ本体を描画 · 生成JS（一部未確定）", render.Label);
        Assert.AreEqual(loop, Next(graph, render));
        var prepare = Next(graph, render, Execute);
        var condition = Next(graph, prepare);
        var applied = Next(graph, condition, "True");
        var skipped = Next(graph, condition, "False");
        var opaque = Next(graph, applied);
        var finish = Next(graph, opaque, "続行する場合");
        Assert.AreEqual("prepare()", prepare.Label);
        Assert.AreEqual((NodeKind.Condition, "enabled"), (condition.Kind, condition.Label));
        Assert.AreEqual("apply()", applied.Label);
        Assert.AreEqual("skip()", skipped.Label);
        Assert.AreEqual(opaque, Next(graph, skipped));
        Assert.AreEqual((NodeKind.Exception, "Razorで生成される処理（未確定）"), (opaque.Kind, opaque.Label));
        Assert.AreEqual("finish()", finish.Label);
        Assert.AreEqual("JS断片の終了", Next(graph, finish).Label);
        Assert.AreEqual(page.Text.IndexOf("@Html.Raw", StringComparison.Ordinal), opaque.Source!.Offset);
        Assert.IsGreaterThanOrEqualTo("@Html.Raw(Model.Fragment)".Length, opaque.Source.Length);
        foreach (var call in new[] { prepare, applied, skipped, finish })
        {
            Assert.IsNull(call.TargetMethod);
            Assert.AreEqual(page.Text.IndexOf(call.Label + ";", StringComparison.Ordinal), call.Source!.Offset);
        }
        Assert.IsTrue(graph.IsPartial);
    }

    [TestMethod]
    [DataRow("#fixed", "change 発生時（候補）")]
    [DataRow("#item_@(i)", "change 発生時（候補・対象要素未確定）")]
    public void QuotedDataElsewhereInTheLoopDoesNotHideExistingBindTargets(string selector, string eventLabel)
    {
        var page = Page($$"""
            <text>
            prepare();
            const label = "@(Model.Title)";
            $("{{selector}}").bind("change", apply);
            finish();
            </text>
            """);
        var graph = Ready(page);
        var execute = Assert.ContainsSingle(graph.Edges.Where(edge => edge.Label == Execute));
        var prepare = graph.Nodes.Single(node => node.Id == execute.To);
        Assert.AreEqual("prepare()", prepare.Label);
        var bind = Next(graph, prepare);
        var call = Next(graph, bind, eventLabel);
        Assert.AreEqual("apply()", call.Label);
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "apply").Id, call.TargetMethod);
        Assert.AreEqual(page.FilePath, call.TargetFilePath);
        Assert.AreEqual("finish()", Next(graph, bind).Label);
    }

    [TestMethod]
    public void UnknownGeneratedConditionDoesNotTakeThePlaceholdersBranch()
    {
        var page = Page("""
            <text>
            prepare("if (ghost) ghost();");
            if (@(Model.Enabled)) { apply(); } else { skip(); }
            finish();
            </text>
            """);
        var graph = Ready(page);
        var condition = Assert.ContainsSingle(graph.Nodes.Where(node => node.Label == "Razorで生成される条件（未確定）"));
        Assert.AreEqual(NodeKind.Condition, condition.Kind);
        Assert.AreEqual("apply()", Next(graph, condition, "True").Label);
        Assert.AreEqual("skip()", Next(graph, condition, "False").Label);
        Assert.DoesNotContain(node => node.Label is "ghost" or "ghost()" or "null", graph.Nodes);
        Assert.IsTrue(graph.IsPartial);
    }

    [TestMethod]
    public void MixedLoopRespectsTheExistingGeneratedOutputLengthCap()
    {
        var page = Page("<text>prepare();" + new string(' ', 8200) + "@(Model.Code); finish();</text>");
        var graph = Ready(page);
        Assert.DoesNotContain(edge => edge.Label == Execute, graph.Edges);
        var summary = Assert.ContainsSingle(graph.Nodes.Where(node => node.Label.Contains("Razor式を含む原文", StringComparison.Ordinal)));
        Assert.IsLessThan(150, summary.Label.Length);
    }

    [TestMethod]
    public void UnparseableShellDoesNotManufactureStatementBoundaries()
    {
        var graph = Ready(Page("""
            <text>
            prepare();
            @(Model.Code) incomplete(
            finish();
            </text>
            """));
        Assert.DoesNotContain(edge => edge.Label == Execute, graph.Edges);
        Assert.Contains(node => node.Label.Contains("Razor式を含む原文", StringComparison.Ordinal), graph.Nodes);
    }

    private static RazorSourceFile Page(string emitted, string newline = "\n")
    {
        var source = $$"""
            <script>
            function prepare() {}
            function apply() {}
            function skip() {}
            function finish() {}
            $(function () {
                @for (var i = 0; i < 3; i++) {
                    {{emitted}}
                }
            });
            </script>
            """;
        return new RazorSourceFile(source.Replace("\n", newline, StringComparison.Ordinal),
            Path.GetFullPath("GeneratedLoop.cshtml"));
    }

    private static FlowDiagram Ready(RazorSourceFile page) =>
        page.Analyze(page.Entries.Single(entry => entry.Name.StartsWith("無名関数", StringComparison.Ordinal)).Id);

    private static FlowNode Next(FlowDiagram graph, FlowNode from, string? label = null) =>
        graph.Nodes.Single(node => node.Id == graph.Edges.Single(edge => edge.From == from.Id && edge.Label == label).To);
}
