using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorJQueryBindTests
{
    [TestMethod]
    [DataRow("""@:jQuery("#field").bind("change", reload);""", false)]
    [DataRow("""<text>$("#field").bind("change.form", { source: "demo" }, function(e) { if (e.target) reload(); });</text>""", true)]
    public void GeneratedChangeRegistrationKeepsItsCallbackSeparateAndNavigable(string markup, bool anonymous)
    {
        var page = Page(markup);
        var graph = Initialization(page);
        var registration = Assert.ContainsSingle(graph.Nodes.Where(node => node.Kind == NodeKind.Call
            && node.Label.Contains(".bind(", StringComparison.Ordinal)));
        var onChange = Assert.ContainsSingle(graph.Edges.Where(edge => edge.From == registration.Id
            && edge.Label == "change 発生時（候補）"));
        var callback = graph.Nodes.Single(node => node.Id == onChange.To);
        var call = anonymous
            ? graph.Nodes.Single(node => node.Id == graph.Edges.Single(edge => edge.From == callback.Id && edge.Label == "True").To)
            : callback;
        if (anonymous) Assert.AreEqual("e.target", callback.Label);
        Assert.AreEqual("reload()", call.Label);
        Assert.AreEqual(page.FilePath, call.TargetFilePath);
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "reload").Id, call.TargetMethod);
        Assert.AreEqual(4, call.Source!.Line);
        Assert.AreEqual(page.Text.IndexOf(anonymous ? "reload();" : "reload);", StringComparison.Ordinal), call.Source.Offset);

        var branchEntry = graph.Edges.Single(edge => edge.To == registration.Id);
        Assert.AreEqual("生成JS（候補）", branchEntry.Label);
        var continuation = graph.Nodes.Single(node => node.Id == graph.Edges.Single(edge =>
            edge.From == registration.Id && edge.Label is null).To);
        Assert.AreEqual("JS断片の終了", continuation.Label);
        Assert.AreNotEqual(callback.Id, continuation.Id);
    }

    [TestMethod]
    [DataRow("""@:$("#field").bind(eventName, reload);""")]
    [DataRow("""@:$("@Model.Field").bind("change", reload);""")]
    [DataRow("""@:other.bind("change", reload);""")]
    [DataRow("""@:$("#field").bind("error load", reload);""")]
    [DataRow("""@:$("#field").bind("change", pickHandler());""")]
    public void UnprovedOrIgnoredRegistrationsDoNotInventChangeCallbacks(string markup)
    {
        var graph = Initialization(Page(markup));
        Assert.DoesNotContain(edge => edge.Label == "change 発生時（候補）", graph.Edges);
        Assert.DoesNotContain(node => node.Label == "reload()" && node.TargetMethod is not null, graph.Nodes);
    }

    [TestMethod]
    public void CallbackParametersDoNotLinkToASameNamedOuterDefinition()
    {
        var graph = Initialization(Page("""@:$("#field").bind("change", function(reload) { reload(); });"""));
        var call = Assert.ContainsSingle(graph.Nodes.Where(node => node.Label == "reload()"));
        Assert.IsNull(call.TargetMethod);
        Assert.Contains(edge => edge.To == call.Id && edge.Label == "change 発生時（候補）", graph.Edges);
    }

    [TestMethod]
    public void OrdinaryChangeBindingWithGeneratedCallbackBodyKeepsTheExistingEventFlow()
    {
        var page = new RazorSourceFile("""
            <script>
            function reload() {}
            $("#field").bind("change", function () {
                @if (Model.Visible) {
                    <text>reload();</text>
                }
            });
            </script>
            """, Path.GetFullPath("OrdinaryBind.cshtml"));
        var binding = Assert.ContainsSingle(page.Analyze(-1).Nodes.Where(node => node.Label == "change · #field"));
        Assert.IsNotNull(binding.TargetMethod);
        var callback = page.Analyze(binding.TargetMethod.Value);
        var call = Assert.ContainsSingle(callback.Nodes.Where(node => node.Label == "reload()"));
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "reload").Id, call.TargetMethod);
    }

    private static RazorSourceFile Page(string markup) => new($$"""
        <script>
        function reload() {}
        @if (Model.Visible) {
            {{markup}}
        }
        after();
        </script>
        """, Path.GetFullPath("GeneratedBind.cshtml"));

    private static FlowDiagram Initialization(RazorSourceFile page) =>
        page.Analyze(page.Entries.Single(entry => entry.Name.StartsWith("JavaScript 初期化", StringComparison.Ordinal)).Id);
}
