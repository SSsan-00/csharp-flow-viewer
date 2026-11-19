using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorRequestTests
{
    private const string PagePath = "/Pages/Order.cshtml";
    private const string ModelPath = "/Pages/Order.cshtml.cs";

    private static RazorSourceFile Page(string text, IReadOnlyList<RazorImport>? imports = null)
    {
        var page = new RazorSourceFile(text, PagePath, imports);
        page.BindHandlers([
            new RazorHandler("OnPostSave", "POST", "Save", ModelPath, 100, new SourceRange(100, 20, 5, ModelPath)),
            new RazorHandler("OnGet", "GET", null, ModelPath, 200, new SourceRange(200, 20, 9, ModelPath))
        ]);
        return page;
    }

    [TestMethod]
    public void XhrRequestLinksPageModelAndKeepsResponseCallbackAsAnEventCandidate()
    {
        var page = Page("""
            @page
            @model OrderModel
            <select onchange="save()"></select>
            <script>
                function save() {
                    var xhr = new XMLHttpRequest();
                    xhr.open("POST", "?handler=Save", true);
                    xhr.onreadystatechange = function () {
                        if (xhr.readyState === 4) showResult(xhr.responseText);
                    };
                    xhr.send("act=save");
                    afterSend();
                }
            </script>
            """);
        var entry = Assert.ContainsSingle(page.Entries.Where(item => item.Name == "save"));
        var flow = page.Analyze(entry.Id);
        var request = Assert.ContainsSingle(flow.Nodes.Where(node => node.TargetFilePath == ModelPath));
        Assert.AreEqual(100, request.TargetMethod);
        StringAssert.Contains(request.Label, "POST");
        StringAssert.Contains(request.Label, "handler=Save");
        Assert.IsNotNull(request.Source);
        Assert.AreEqual("xhr.send(\"act=save\")", page.Text.Substring(request.Source.Offset, request.Source.Length));
        var response = Assert.ContainsSingle(flow.Nodes.Where(node => node.Label.StartsWith("応答イベント候補:", StringComparison.Ordinal)));
        Assert.IsTrue(response.TargetMethod < -1);
        Assert.Contains(edge => edge.From == request.Id && edge.To == response.Id
            && edge.Label == "非同期の応答イベント候補", flow.Edges);
        Assert.Contains(node => node.Label.Contains("afterSend()", StringComparison.Ordinal), flow.Nodes);
    }

    [TestMethod]
    public void AspPageHandlerRequiresAnEnabledBuiltInTagHelper()
    {
        const string markup = """
            @page
            @model OrderModel
            <form method="post"><button asp-page-handler="Save">Save</button></form>
            """;
        var noHelpers = Page(markup);
        Assert.DoesNotContain(node => node.TargetFilePath == ModelPath, noHelpers.Analyze(-1).Nodes);

        var imports = new[] { new RazorImport("/Pages/_ViewImports.cshtml",
            "@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers") };
        var enabled = Page(markup, imports);
        Assert.Contains(node => node.TargetMethod == 100 && node.TargetFilePath == ModelPath, enabled.Analyze(-1).Nodes);

        var removed = Page(markup.Replace("@model OrderModel", "@model OrderModel\n@removeTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers", StringComparison.Ordinal), imports);
        Assert.DoesNotContain(node => node.TargetFilePath == ModelPath, removed.Analyze(-1).Nodes);
    }

    [TestMethod]
    [DataRow("<form asp-page-handler=\"Save\"><button>Save</button></form>", "POST")]
    [DataRow("<form asp-page=\"./Order\"><button>Save</button></form>", "POST")]
    [DataRow("<form asp-page-handler=\"Save\" method=\"get\"><button>Save</button></form>", "GET")]
    [DataRow("<form><button asp-page-handler=\"Save\">Save</button></form>", "GET")]
    [DataRow("<form><button>Save</button></form>", "GET")]
    public void OmittedFormMethodUsesTheActiveTagHelperGenerationRules(string formMarkup, string expectedMethod)
    {
        var page = Page("@page\n@model OrderModel\n" + formMarkup,
            [new RazorImport("/Pages/_ViewImports.cshtml", "@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers")]);
        var form = Assert.ContainsSingle(page.Entries.Where(entry => entry.Name == "submit"));
        var flow = page.Analyze(form.Id);
        var request = Assert.ContainsSingle(flow.Nodes.Where(node => node.Kind == NodeKind.Transfer));
        StringAssert.StartsWith(request.Label, expectedMethod + " · ");
        if (formMarkup.StartsWith("<form asp-page-handler=\"Save\">", StringComparison.Ordinal))
            Assert.Contains(node => node.TargetFilePath == ModelPath && node.TargetMethod == 100, flow.Nodes);
    }

    [TestMethod]
    [DataRow("<form asp-controller=\"Other\" asp-action=\"Save\" method=\"post\"></form>", "submit")]
    [DataRow("<form method=\"post\"><button asp-controller=\"Other\" asp-action=\"Save\">Save</button></form>", "click")]
    [DataRow("<a asp-controller=\"Other\" asp-action=\"Save\">Save</a>", "click")]
    [DataRow("<form asp-route=\"OtherRoute\" method=\"post\"></form>", "submit")]
    [DataRow("<form method=\"post\"><button asp-route=\"OtherRoute\">Save</button></form>", "click")]
    [DataRow("<a asp-route=\"OtherRoute\">Save</a>", "click")]
    public void MvcAndNamedRoutesNeverFallBackToTheCurrentPageModel(string markup, string eventName)
    {
        var page = Page("@page\n@model OrderModel\n" + markup,
            [new RazorImport("/Pages/_ViewImports.cshtml", "@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers")]);
        // Default handlers deliberately exist: the test must catch an incorrect
        // current-page fallback instead of passing because no method was available.
        page.BindHandlers([
            new RazorHandler("OnPost", "POST", null, ModelPath, 300, new SourceRange(300, 20, 12, ModelPath)),
            new RazorHandler("OnGet", "GET", null, ModelPath, 200, new SourceRange(200, 20, 9, ModelPath))
        ]);
        var entry = Assert.ContainsSingle(page.Entries.Where(candidate => candidate.Name == eventName));
        var flow = page.Analyze(entry.Id);
        Assert.DoesNotContain(node => node.TargetFilePath == ModelPath, flow.Nodes);
        Assert.Contains(node => node.Label.Contains("MVC / 名前付きルート（未解決）", StringComparison.Ordinal), flow.Nodes);
    }

    [TestMethod]
    public void FragmentLinksAndRelativeRequestsWithBaseHrefDoNotAcquireFalseServerTargets()
    {
        var page = Page("""
            @page
            @model OrderModel
            <base href="/another/">
            <a href="#details">Details</a>
            <script>
                function save() {
                    var xhr = new XMLHttpRequest();
                    xhr.open("POST", "?handler=Save", true);
                    xhr.send();
                }
            </script>
            """);
        var anchor = Assert.ContainsSingle(page.Entries.Where(entry => entry.Signature.Contains("Details", StringComparison.Ordinal)));
        var link = page.Analyze(anchor.Id);
        Assert.Contains(node => node.Label.Contains("ページ内移動", StringComparison.Ordinal), link.Nodes);
        Assert.DoesNotContain(node => node.TargetFilePath == ModelPath, link.Nodes);
        var save = Assert.ContainsSingle(page.Entries.Where(entry => entry.Name == "save"));
        Assert.DoesNotContain(node => node.TargetFilePath == ModelPath, page.Analyze(save.Id).Nodes);
    }

    [TestMethod]
    public void RazorValueInsideScriptPreservesUnrelatedFunctionsWithoutClaimingConstantValues()
    {
        var page = Page("""
            @page
            @model OrderModel
            <select onchange="changed()"></select>
            <script>
                var orderId = @Model.Id;
                function changed() {
                    if (orderId) show(orderId);
                }
            </script>
            """);
        var function = Assert.ContainsSingle(page.Entries.Where(entry => entry.Name == "changed"));
        var flow = page.Analyze(function.Id);
        Assert.IsTrue(flow.IsPartial);
        Assert.Contains(node => node.Kind == NodeKind.Condition && node.Label == "orderId", flow.Nodes);
        Assert.Contains(notice => notice.Contains("Razor", StringComparison.Ordinal), flow.Notices);
        Assert.DoesNotContain(node => node.TargetFilePath == ModelPath, flow.Nodes);
    }
}
