using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorGeneratedIdBindTests
{
    private const string Candidate = "change 発生時（候補・対象要素未確定）";

    [TestMethod]
    [DataRow("""$("#item_@(i)").bind("change", reload);""", "reload);")]
    [DataRow("""jQuery('#item_@i').bind("change.form", { source: "demo" }, function () { reload(); });""", "reload();")]
    [DataRow("""$("#section_@(Model.Section)_@(i)").bind("change", reload);""", "reload);")]
    public void ReadyLoopWithRazorGeneratedIdLinksToTheStaticHandler(string registration, string handlerSource)
    {
        var page = Page(registration);
        var initialization = page.Entries.Single(entry => entry.Name.StartsWith("JavaScript 初期化", StringComparison.Ordinal));
        var ready = page.Entries.Single(entry => entry.Name.StartsWith("無名関数", StringComparison.Ordinal));
        var definition = page.Entries.Single(entry => entry.Name == "reload");
        foreach (var entry in new[] { -1, initialization.Id, ready.Id })
        {
            var graph = page.Analyze(entry);
            var link = Assert.ContainsSingle(graph.Edges.Where(edge => edge.Label == Candidate));
            var bind = graph.Nodes.Single(node => node.Id == link.From);
            var call = graph.Nodes.Single(node => node.Id == link.To);
            Assert.AreEqual(NodeKind.Call, bind.Kind);
            Assert.AreEqual(registration.TrimEnd(';'), bind.Label);
            Assert.AreEqual("reload()", call.Label);
            Assert.AreEqual(definition.Id, call.TargetMethod);
            Assert.AreEqual(page.FilePath, call.TargetFilePath);
            Assert.AreEqual(page.Text.IndexOf(handlerSource, StringComparison.Ordinal), call.Source!.Offset);
            Assert.IsTrue(graph.IsPartial);
            Assert.Contains(notice => notice.Contains("動的な値を確定できません", StringComparison.Ordinal), graph.Notices);
        }
        Assert.DoesNotContain(entry => entry.Signature.Contains("change ·", StringComparison.Ordinal), page.Entries);
    }

    [TestMethod]
    [DataRow("""$("#item_@(i)").bind(eventName, reload);""", "")]
    [DataRow("""$("#item_@(i)").bind("@(Model.EventName)", reload);""", "")]
    [DataRow("""$("#item_@(i)").bind("error load", reload);""", "")]
    [DataRow("""$("#item_@(i)").bind("change", missing);""", "")]
    [DataRow("""$("#item_@(i)").bind("change", reload);""", "const $ = customSelector;")]
    [DataRow("""other("#item_@(i)").bind("change", reload);""", "")]
    [DataRow("""$("div.@(i)").bind("change", reload);""", "")]
    [DataRow("""$("#item_@(Html.Raw(i))").bind("change", reload);""", "")]
    [DataRow("""$("#item_@(i)").bind("change", reload); loadOther(@(Model.Id));""", "")]
    public void UnprovedBindingsDoNotCreateAChangePathOrTarget(string registration, string prefix)
    {
        var page = Page(registration, prefix);
        var entry = page.Entries.Single(entry => entry.Name.StartsWith("JavaScript 初期化", StringComparison.Ordinal));
        var graph = page.Analyze(entry.Id);
        Assert.DoesNotContain(edge => edge.Label?.StartsWith("change 発生時", StringComparison.Ordinal) == true, graph.Edges);
        Assert.DoesNotContain(node => node.Label == "reload()" && node.TargetMethod is not null, graph.Nodes);
    }

    private static RazorSourceFile Page(string registration, string prefix = "") => new($$"""
        @page
        <script>
        function reload() {}
        $(function () {
            {{prefix}}
            @for (var i = 0; i < 3; i++) {
                <text>{{registration}}</text>
            }
        });
        </script>
        """, Path.GetFullPath("DynamicIdBind.cshtml"));
}