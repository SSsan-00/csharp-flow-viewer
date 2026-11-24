using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorScriptRecoveryTests
{
    [TestMethod]
    [DataRow("const broken = ;", "")]
    [DataRow("const broken;", "")]
    [DataRow("", "let broken=;")]
    [DataRow("const first = ;", "var second=/* no value */;")]
    [DataRow("function unrelated() { var broken=; }", "")]
    public void InlineChangeReachesAnIntactDefinitionAcrossAnUnrelatedSyntaxError(string before, string after)
    {
        var page = Page($$"""
            {{before}}
            function reload() {
                return 11;
            }
            {{after}}
            """);
        var definition = Assert.ContainsSingle(page.Entries.Where(entry => entry.Name == "reload"));
        var overview = EventNode(page);
        Assert.AreEqual(definition.Id, overview.TargetMethod);
        Assert.AreEqual(page.FilePath, overview.TargetFilePath);
        var eventEntry = page.Entries.Single(entry => page.IsInlineEvent(entry.Id));
        var detail = Assert.ContainsSingle(page.Analyze(eventEntry.Id).Nodes.Where(node => node.Label == "reload()"));
        Assert.AreEqual(definition.Id, detail.TargetMethod);
        var flow = page.Analyze(definition.Id);
        var result = Assert.ContainsSingle(flow.Nodes.Where(node => node.Label == "return 11;"));
        Assert.AreEqual(4, definition.Source.Line);
        Assert.AreEqual(page.Text.IndexOf("return 11;", StringComparison.Ordinal), result.Source!.Offset);
        Assert.AreEqual(5, result.Source.Line);
        Assert.IsTrue(flow.IsPartial);
        Assert.Contains(notice => notice.Contains("JavaScript構文エラー", StringComparison.Ordinal), page.Notices);
    }

    [TestMethod]
    [DataRow("""const broken = ; const code = "function reload() {}"; // function reload() {}""", 0)]
    [DataRow("""const broken = ; const pattern = /function reload\(\)/; /* function reload() {} */""", 0)]
    [DataRow("function outer() { const broken = ; function reload() {} }", 1)]
    [DataRow("const broken = ; function reload() {} function reload() {}", 2)]
    [DataRow("function reload() {} reload=;", 1)]
    [DataRow("function reload() {} window.reload = ;", 1)]
    [DataRow("const reload; function elsewhere() { function reload() {} }", 1)]
    [DataRow("var reload = ; function elsewhere() { function reload() {} }", 1)]
    public void RecoveryDoesNotInventTargetsOrIgnoreMalformedWrites(string script, int definitions)
    {
        var page = Page(script);
        Assert.AreEqual(definitions, page.Entries.Count(entry => entry.Name == "reload"));
        if (definitions == 0)
            Assert.Contains(entry => entry.Name.StartsWith("JavaScript 初期化", StringComparison.Ordinal), page.Entries);
        Assert.IsNull(EventNode(page).TargetMethod);
        Assert.Contains(notice => notice.Contains("JavaScript構文エラー", StringComparison.Ordinal), page.Notices);
    }

    [TestMethod]
    public void TargetWithMalformedBodyRetainsItsDefinitionButNotAClaimOfCompleteness()
    {
        var page = Page("function reload() { let broken=; return 11; }");
        var target = Assert.ContainsSingle(page.Entries.Where(entry => entry.Name == "reload"));
        Assert.AreEqual(target.Id, EventNode(page).TargetMethod);
        var flow = page.Analyze(target.Id);
        Assert.IsTrue(flow.IsPartial);
        Assert.Contains(node => node.Label == "broken=", flow.Nodes);
        Assert.DoesNotContain(node => node.Label == "broken=0", flow.Nodes);
        Assert.Contains(node => node.Label == "return 11;", flow.Nodes);
    }

    [TestMethod]
    public void ScalarRazorExpressionAndUnrelatedJavaScriptErrorKeepTheirOriginalSourceRanges()
    {
        var page = Page("""
            const broken;
            const seed = @Model.Seed;
            function reload() { return seed; }
            """);
        Assert.IsNotNull(EventNode(page).TargetMethod);
        var init = page.Entries.Single(entry => entry.Name.StartsWith("JavaScript 初期化", StringComparison.Ordinal));
        var graph = page.Analyze(init.Id);
        var seed = Assert.ContainsSingle(graph.Nodes.Where(node => node.Label == "seed = @Model.Seed"));
        Assert.AreEqual(page.Text.IndexOf(seed.Label, StringComparison.Ordinal), seed.Source!.Offset);
        Assert.AreEqual(seed.Label, page.Text.Substring(seed.Source.Offset, seed.Source.Length));
        Assert.AreEqual(4, seed.Source.Line);
        Assert.IsTrue(graph.IsPartial);
    }

    [TestMethod]
    [DataRow("const broken = \"unterminated;\nfunction reload() {}")]
    [DataRow("function other() { const broken=;\nfunction reload() {}")]
    [DataRow("const a=; const b=; const c=; const d=; const e=; function reload() {}")]
    public void IncompleteBoundariesAndExcessiveErrorsRemainUnresolved(string script)
    {
        var page = Page(script);
        Assert.DoesNotContain(entry => entry.Name == "reload", page.Entries);
        Assert.IsNull(EventNode(page).TargetMethod);
        Assert.Contains(notice => notice.Contains("JavaScript構文エラー", StringComparison.Ordinal), page.Notices);
    }

    private static RazorSourceFile Page(string script) => new($$"""
        <select onchange="reload();"></select>
        <script language="javascript">
        {{script}}
        </script>
        """, Path.GetFullPath("Index.cshtml"));

    private static FlowNode EventNode(RazorSourceFile page) =>
        Assert.ContainsSingle(page.Analyze(-1).Nodes.Where(node => node.Label == "onchange → reload()"));
}
