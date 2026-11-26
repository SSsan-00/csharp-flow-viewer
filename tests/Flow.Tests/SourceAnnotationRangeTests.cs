using Flow.Analysis;
using Flow.Desktop;
using Flow.Model;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class SourceAnnotationRangeTests
{
    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void AutoMarkColorsOriginalRazorAndScriptRowsWithoutColoringEitherBranch(string newline)
    {
        var source = """
            <button onclick="run()">Run</button>
            <script>
            function run() {
                prepare();
                @if (Model.AddScript) {
                  <text>onlyGenerated();</text>
                }
                if (left ===
                    right) {
                    onlyLeft();
                } else {
                    onlyRight();
                }
                save();
                finish();
            }
            </script>
            """.ReplaceLineEndings(newline);
        var page = new RazorSourceFile(source, Path.GetFullPath("MarkedRows.cshtml"));
        var entry = page.Analyze(-1).Nodes.Single(node => node.Label == "onclick → run()");
        var graph = page.Analyze(entry.TargetMethod!.Value);
        var finish = graph.Nodes.Single(node => node.Label == "finish()");
        var annotations = new NodeAnnotations { Marked = [finish.Id] };
        var visible = annotations.VisibleMarks(graph);
        var spans = new Dictionary<string, SourceAnnotationRange>();
        foreach (var node in graph.Nodes)
            if (Paint(node, source, annotations.Marked.Contains(node.Id), visible.Contains(node.Id)) is { } span)
                spans.Add(node.Label, span);

        Assert.IsTrue(visible.Contains(graph.Nodes.Single(node => node.Kind == NodeKind.Start).Id));
        Assert.DoesNotContain(span => source.AsSpan(span.Offset, span.Length).ContainsAny('\r', '\n'), spans.Values);
        Assert.AreEqual("prepare()", Slice(spans["prepare()"]));
        Assert.AreEqual("save()", Slice(spans["save()"]));
        Assert.AreEqual("finish()", Slice(spans["finish()"]));
        Assert.AreEqual("left ===", Slice(spans.Single(pair => pair.Key.StartsWith("left ===", StringComparison.Ordinal)).Value));
        var razor = spans.Single(pair => pair.Key.Contains("Razorが生成するJavaScript", StringComparison.Ordinal)).Value;
        Assert.AreEqual("if (Model.AddScript) {", Slice(razor));
        Assert.DoesNotContain(span => Slice(span).Contains("only", StringComparison.Ordinal)
            || Slice(span).Contains("function run", StringComparison.Ordinal), spans.Values);

        string Slice(SourceAnnotationRange span) => source.Substring(span.Offset, span.Length);
    }

    [TestMethod]
    public void ExplicitAndGraySpansStayMultilineAndMissingSourceDoesNotPaint()
    {
        const string source = "if (\r\n  condition\r\n) {\r\n\tact();\r\n}";
        var node = new FlowNode("if", NodeKind.Condition, "", new SourceRange(0, 19, 1));
        Assert.AreEqual(new SourceAnnotationRange(0, 4, true), Paint(node, source, visibleMark: true));
        Assert.AreEqual(new SourceAnnotationRange(0, 19, true), Paint(node, source, explicitMark: true, gray: true));
        Assert.AreEqual(new SourceAnnotationRange(0, 19, false), Paint(node, source, visibleMark: true, gray: true));
        Assert.IsNull(Paint(node with { Kind = NodeKind.Start }, source, visibleMark: true));
        Assert.IsNull(Paint(node with { Kind = NodeKind.End }, source, visibleMark: true));
        Assert.IsNull(Paint(node with { Source = null }, source, visibleMark: true));
    }

    private static SourceAnnotationRange? Paint(FlowNode node, string source, bool explicitMark = false,
        bool visibleMark = false, bool gray = false)
    {
        var text = SourceText.From(source);
        return SourceAnnotationRange.ForNode(node, explicitMark, visibleMark, gray, text.Length,
            offset => text.Lines.GetLineFromPosition(offset).End);
    }
}
