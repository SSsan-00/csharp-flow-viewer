using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorScriptRenderingTests
{
    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void JavaScriptFunctionShowsOnlyItsRazorFlowWithoutExecutingEmittedAlternatives(string newline)
    {
        var source = """
            @page
            <script>
            @if (Model.ScriptReady)
            {
                @:init();
            }
            function update() {
                before();
                @if (Model.Enabled)
                {
                    @:enable();
                }
                else
                {
                    <text>disable();</text>
                }
                @for (var index = 0; index < Model.Total; index++)
                {
                    <text>append(@index);</text>
                }
                if (window.confirmed) browserAction();
                after();
            }
            function sibling() {
                @while (Model.SiblingReady)
                {
                    @:siblingAction();
                }
            }
            </script>
            """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        var path = Path.GetFullPath("ScriptControls.cshtml");
        var page = new RazorSourceFile(source, path);
        var graph = Analyze(page, "update");
        var enabled = Condition(graph, "Model.Enabled");
        var loop = Condition(graph, "index < Model.Total");
        Assert.IsNotNull(enabled.Source);
        Assert.AreEqual(path, enabled.Source.FilePath);
        Assert.AreEqual(9, enabled.Source.Line);
        Assert.AreEqual("Model.Enabled", Slice(source, enabled.Source));

        var yes = Next(graph, enabled, "True");
        var no = Next(graph, enabled, "False");
        Assert.IsNotNull(yes.Source);
        Assert.IsNotNull(no.Source);
        StringAssert.Contains(Slice(source, yes.Source), "enable();");
        StringAssert.Contains(Slice(source, no.Source), "disable();");
        Assert.AreEqual(Next(graph, yes), Next(graph, no));
        Assert.AreEqual(loop, Next(graph, Next(graph, yes)));
        Assert.AreEqual(loop, Next(graph, Next(graph, Next(graph, loop, "True"))));
        Assert.AreEqual(NodeKind.End, Next(graph, loop, "False").Kind);

        Assert.IsTrue(Condition(graph, "window.confirmed").IsReachable);
        Assert.Contains(node => node.Label == "before()", graph.Nodes);
        Assert.Contains(node => node.Label == "after()", graph.Nodes);
        Assert.AreEqual(2, graph.Nodes.Count(node => node.Label.Contains("実行経路未解決", StringComparison.Ordinal)));
        Assert.DoesNotContain(node => node.Label is "enable()" or "disable()" or "append(@index)", graph.Nodes);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Condition && node.Label.Contains("SiblingReady", StringComparison.Ordinal), graph.Nodes);
        Assert.DoesNotContain(edge => IsRender(edge.From) != IsRender(edge.To), graph.Edges);
        Assert.DoesNotContain(notice => notice.Contains("JavaScript構文エラー", StringComparison.Ordinal), graph.Notices);

        var initial = Analyze(page, "JavaScript 初期化");
        Condition(initial, "Model.ScriptReady");
        Assert.AreEqual(1, initial.Nodes.Count(node => node.Kind == NodeKind.Condition && IsRender(node.Id)));
        Condition(Analyze(page, "sibling"), "Model.SiblingReady");
        Condition(page.Analyze(-1), "Model.Enabled");
    }

    [TestMethod]
    public void UnparseableBrowserProjectionKeepsRazorScriptEntryAndItsBothPaths()
    {
        var page = new RazorSourceFile("""
            @page
            <script>
            let value =
                @if (Model.Enabled)
                {
                    <text>1</text>
                }
                else
                {
                    <text>0</text>
                }
                ;
            </script>
            """, Path.GetFullPath("ScriptExpression.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        var condition = Condition(graph, "Model.Enabled");
        Assert.AreEqual(Next(graph, Next(graph, condition, "True")), Next(graph, Next(graph, condition, "False")));
        Assert.IsTrue(graph.IsPartial);
        Assert.Contains(node => node.Kind == NodeKind.Exception && !IsRender(node.Id), graph.Nodes);
        Assert.Contains(notice => notice.Contains("JavaScript構文エラー", StringComparison.Ordinal), graph.Notices);
        Assert.DoesNotContain(edge => IsRender(edge.From) != IsRender(edge.To), graph.Edges);
    }

    [TestMethod]
    public void RazorSlotConsumedAsReturnTerminatorDoesNotInventVoidReturn()
    {
        var page = new RazorSourceFile("""
            @page
            <script>
            function selected() {
                return @if (Model.Enabled)
                {
                    <text>1;</text>
                }
                else
                {
                    <text>2;</text>
                }
            }
            </script>
            """, Path.GetFullPath("ScriptReturnExpression.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        Condition(graph, "Model.Enabled");
        Assert.DoesNotContain(node => node.Kind == NodeKind.Transfer && !IsRender(node.Id), graph.Nodes);
        Assert.Contains(notice => notice.Contains("独立したJavaScript文として確定できません", StringComparison.Ordinal), graph.Notices);
    }

    [TestMethod]
    public void StandaloneRazorSlotDoesNotTurnFollowingJsIntoUnbracedIfBody()
    {
        var page = new RazorSourceFile("""
            @page
            <script>
            if (browserReady)
                @if (Model.Enabled)
                {
                    @:emit();
                }
            tail();
            </script>
            """, Path.GetFullPath("UnbracedBrowserIf.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        var browserIf = Condition(graph, "browserReady");
        var boundary = Next(graph, browserIf, "True");
        Assert.AreEqual("tail()", Next(graph, browserIf, "False").Label);
        Assert.AreEqual(NodeKind.Exception, boundary.Kind);
        Assert.AreEqual("tail()", Next(graph, boundary, "続行する場合").Label);
        Assert.DoesNotContain(node => node.Label == "emit()", graph.Nodes);
    }

    [TestMethod]
    public void RazorOutsideScriptDoesNotEraseBrowserFunctions()
    {
        var page = new RazorSourceFile("""
            @page
            @if (Model.ShowScript)
            {
                <script>
                function run() {
                    if (window.ready) execute();
                }
                </script>
            }
            """, Path.GetFullPath("GuardedScript.cshtml"));
        var graph = Analyze(page, "run");
        Condition(graph, "window.ready");
        Assert.Contains(node => node.Label == "execute()", graph.Nodes);
        Assert.DoesNotContain(node => IsRender(node.Id), graph.Nodes);
        Condition(page.Analyze(-1), "Model.ShowScript");
    }

    [TestMethod]
    public void UnsupportedRazorTryDoesNotPromoteNestedIfOrFlattenCatch()
    {
        var page = new RazorSourceFile("""
            @page
            <script>
            @try
            {
                if (Model.Enabled)
                {
                    @:whenEnabled();
                }
            }
            catch
            {
                @:recover();
            }
            tail();
            </script>
            """, Path.GetFullPath("ScriptTry.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        Assert.Contains(node => IsRender(node.Id) && node.Label.Contains("try / catch", StringComparison.Ordinal), graph.Nodes);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Condition && IsRender(node.Id), graph.Nodes);
        Assert.DoesNotContain(node => node.Label is "whenEnabled()" or "recover()", graph.Nodes);
        Assert.Contains(node => node.Label == "tail()", graph.Nodes);
    }

    [TestMethod]
    public void RenderingDisplayLimitDoesNotLeakBranchOutputIntoBrowserFlow()
    {
        var controls = string.Join("\n", Enumerable.Range(0, 101).Select(i => $$"""
            @if (Model.Check({{i}}))
            {
                @:onlyCandidate{{i}}();
            }
            """));
        var page = new RazorSourceFile($"@page\n<script>\n{controls}\n tail();\n</script>",
            Path.GetFullPath("LargeMixedScript.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        Assert.AreEqual(100, graph.Nodes.Count(node => node.Kind == NodeKind.Condition && IsRender(node.Id)));
        Assert.AreEqual(101, graph.Nodes.Count(node => node.Label.Contains("実行経路未解決", StringComparison.Ordinal)));
        Assert.DoesNotContain(node => node.Label.StartsWith("onlyCandidate", StringComparison.Ordinal), graph.Nodes);
        Assert.Contains(node => node.Label == "tail()", graph.Nodes);
    }

    private static FlowDiagram Analyze(RazorSourceFile page, string name) =>
        page.Analyze(page.Entries.Single(entry => entry.Name.StartsWith(name, StringComparison.Ordinal)).Id);
    private static FlowNode Condition(FlowDiagram graph, string text) =>
        graph.Nodes.Single(node => node.Kind == NodeKind.Condition && node.Label.Contains(text, StringComparison.Ordinal));
    private static FlowNode Next(FlowDiagram graph, FlowNode node, string? label = null) =>
        graph.Nodes.Single(target => target.Id == graph.Edges.Single(edge => edge.From == node.Id && edge.Label == label).To);
    private static string Slice(string source, SourceRange range) => source.Substring(range.Offset, range.Length);
    private static bool IsRender(string id) => id.StartsWith("render_", StringComparison.Ordinal);
}
