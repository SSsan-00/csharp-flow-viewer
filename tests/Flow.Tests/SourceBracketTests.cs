using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class SourceBracketTests
{
    [TestMethod]
    public void TheBracketUnderTheCaretTakesPrecedenceOverAnAdjacentPair()
    {
        SourceBracket[] pairs = [new(2, 6), new(3, 5)];
        Assert.AreEqual(5, SourceBracket.MatchAt(pairs, 3));
        Assert.AreEqual(2, SourceBracket.MatchAt(pairs, 6));
        Assert.AreEqual(2, SourceBracket.MatchAt(pairs, 7));
        Assert.IsNull(SourceBracket.MatchAt(pairs, 1));
    }

    [TestMethod]
    public void CSharpPairsNestedTokensButNotCommentsAndLiteralText()
    {
        const string source = """
            class Demo
            {
                void Run(int[] values)
                {
                    var literal = "(fake)";
                    // [comment]
                    if ((values[0]) > 0) {
                        Run(values);
                    }
                }
            }
            """;
        var brackets = AnalysisTests.File(source).Brackets;
        AssertPair(source, brackets, "((values", 0, "> 0)", 3);
        AssertPair(source, brackets, "((values", 1, "[0])", 3);
        AssertPair(source, brackets, "[0]", 0, "[0]", 2);
        var block = AssertPair(source, brackets, "> 0) {", 5, "        }", 8);
        Assert.IsTrue(block.CanFold);
        Assert.IsNull(SourceBracket.MatchAt(brackets, source.IndexOf("(fake)", StringComparison.Ordinal)));
        Assert.IsNull(SourceBracket.MatchAt(brackets, source.IndexOf("[comment]", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void RazorUsesOriginalOffsetsForCSharpAndJavaScript(string newline)
    {
        var source = """
            @if (Model.Add(Model.Values[0]))
            {
                <p>@(Model.Values[1])</p>
            }
            <script>
            function run(value) {
                const sample = "{literal}";
                // (comment)
                if (/[()]/.test(value)) {
                    const output = `{plain} ${select([value])}`;
                    save(output);
                }
            }
            </script>
            """.ReplaceLineEndings(newline);
        var page = new RazorSourceFile(source, Path.GetFullPath("Pair.cshtml"));
        var brackets = page.Brackets;
        AssertPair(source, brackets, "(Model.Add", 0, "0]))", 3);
        Assert.IsTrue(AssertPair(source, brackets, newline + "{", newline.Length,
            newline + "}" + newline + "<script>", newline.Length).CanFold);
        AssertPair(source, brackets, "@(Model", 1, "1])", 2);
        AssertPair(source, brackets, "[1]", 0, "[1]", 2);
        var js = AssertPair(source, brackets, "value) {", 7, "}" + newline + "</script>", 0);
        Assert.IsTrue(js.CanFold);
        Assert.IsFalse(AssertPair(source, brackets, "${select", 1, "value])}", 7).CanFold);
        AssertPair(source, brackets, "[value]", 0, "[value]", 6);

        foreach (var ignored in new[] { "{literal}", "(comment)", "[()]", "{plain}" })
            Assert.IsNull(SourceBracket.MatchAt(brackets, source.IndexOf(ignored, StringComparison.Ordinal)), ignored);
    }

    [TestMethod]
    public void PiecesFromDifferentScriptsAndRecoveryDoNotPair()
    {
        const string source = """
            <script>function broken(</script>
            <script>function recover() { const value = ; }</script>
            <script>function valid() { if (true) { call(); } }</script>
            """;
        var brackets = new RazorSourceFile(source, Path.GetFullPath("Unbalanced.cshtml")).Brackets;
        Assert.IsNull(SourceBracket.MatchAt(brackets, source.IndexOf("broken(", StringComparison.Ordinal) + 6));
        Assert.IsNull(SourceBracket.MatchAt(brackets, source.IndexOf("recover()", StringComparison.Ordinal) + 7));
        AssertPair(source, brackets, "valid()", 5, "valid()", 6);
        AssertPair(source, brackets, "if (true)", 3, "if (true)", 8);
    }

    private static SourceBracket AssertPair(string source, IReadOnlyList<SourceBracket> pairs, string openText,
        int openOffset, string closeText, int closeOffset)
    {
        var begin = source.IndexOf(openText, StringComparison.Ordinal);
        var end = source.IndexOf(closeText, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, openText);
        Assert.IsGreaterThanOrEqualTo(0, end, closeText);
        begin += openOffset;
        end += closeOffset;
        Assert.AreEqual(end, SourceBracket.MatchAt(pairs, begin), openText);
        Assert.AreEqual(begin, SourceBracket.MatchAt(pairs, end), closeText);
        return pairs.Single(pair => pair.Open == begin && pair.Close == end);
    }
}
