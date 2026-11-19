using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorMarkupTests
{
    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void ExplicitMarkupTransitionsKeepOriginalLocationsAndRenderingConditions(string newline)
    {
        var text = """
            @page
            @model ExampleModel
            <form method="post">
            @if (Model.Visible)
            {
                @:<button id="single" type="submit">Single line</button>
                var example = "<button id=\"not-markup\" type=\"submit\">C# string</button>";
                <text>
                    <button id="multiple" type="submit">Multiple lines</button>
                </text>
            }
            </form>
            """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        var path = Path.GetFullPath("TransitionExample.cshtml");
        var page = new RazorSourceFile(text, path);

        Assert.AreEqual(-1, page.Entries[0].Id);
        foreach (var identifier in new[] { "single", "multiple" })
        {
            var start = text.IndexOf("<button id=\"" + identifier + "\"", StringComparison.Ordinal);
            var entry = Assert.ContainsSingle(page.Entries.Where(item => item.Source.Offset == start));
            StringAssert.Contains(entry.Signature, "#" + identifier);
            Assert.AreEqual(path, entry.Source.FilePath);
            Assert.AreEqual(text[..start].Count(character => character == '\n') + 1, entry.Source.Line);
            StringAssert.StartsWith(text.Substring(entry.Source.Offset, entry.Source.Length), "<button");

            var flow = page.Analyze(entry.Id);
            var guard = Assert.ContainsSingle(flow.Nodes.Where(node => node.Kind == NodeKind.Condition));
            StringAssert.Contains(guard.Label, "Model.Visible");
            Assert.IsNotNull(guard.Source);
            Assert.AreEqual("Model.Visible", text.Substring(guard.Source.Offset, guard.Source.Length));
            Assert.AreEqual(path, guard.Source.FilePath);
        }

        // @: switches back at the newline; the following C# string is not another HTML element.
        Assert.DoesNotContain(entry => entry.Signature.Contains("not-markup", StringComparison.Ordinal), page.Entries);
        Assert.AreEqual(4, page.Entries.Count, "Expected the overview, form, and two real submit buttons.");
        Assert.IsEmpty(page.Notices.Where(notice => notice.StartsWith("Razor ·", StringComparison.Ordinal)),
            "Valid @: and <text> blocks must not create Razor parser diagnostics.");
    }

    [TestMethod]
    public void RazorCommentsAndJavaScriptStringsDoNotCreateHtmlEvents()
    {
        var text = """
            @page
            @* <button id="comment" onclick="wrong()">Hidden Razor comment</button> *@
            <script>
                const template = '<button id="template" onclick="wrong()">Template</button>';
                function save() { return 1; }
            </script>
            @{
                <text>
                    <button id="actual" type="button" onclick="save()">Actual</button>
                </text>
            }
            """;
        var page = new RazorSourceFile(text, Path.GetFullPath("CommentExample.cshtml"));
        var overview = page.Analyze(-1);
        var eventNode = Assert.ContainsSingle(overview.Nodes.Where(node => node.Label.Contains("#actual", StringComparison.Ordinal)));
        Assert.IsNotNull(eventNode.TargetMethod);
        Assert.DoesNotContain(node => node.Label.Contains("#comment", StringComparison.Ordinal)
            || node.Label.Contains("#template", StringComparison.Ordinal), overview.Nodes);

        var handler = page.Entries.Single(entry => entry.Id == eventNode.TargetMethod);
        Assert.AreEqual("save()", text.Substring(handler.Source.Offset, handler.Source.Length));
    }
}
