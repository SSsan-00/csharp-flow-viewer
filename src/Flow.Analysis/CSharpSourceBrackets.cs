using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Flow.Analysis;

internal static class CSharpSourceBrackets
{
    public static IEnumerable<SourceBracket> Read(SyntaxNode root, SourceText source, int shift, CancellationToken token)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            token.ThrowIfCancellationRequested();
            foreach (var opening in node.ChildTokens())
            {
                if (opening.IsMissing) continue;
                var (kind, first, last) = opening.Kind() switch
                {
                    SyntaxKind.OpenParenToken => (SyntaxKind.CloseParenToken, '(', ')'),
                    SyntaxKind.OpenBracketToken => (SyntaxKind.CloseBracketToken, '[', ']'),
                    SyntaxKind.OpenBraceToken => (SyntaxKind.CloseBraceToken, '{', '}'),
                    _ => default
                };
                if (kind == SyntaxKind.None) continue;
                var closing = node.ChildTokens().FirstOrDefault(t => t.SpanStart > opening.SpanStart && t.IsKind(kind));
                if (closing.RawKind == 0 || closing.IsMissing) continue;
                var start = opening.SpanStart + shift;
                var end = closing.SpanStart + shift;
                if (start >= 0 && end < source.Length && source[start] == first && source[end] == last)
                    yield return new SourceBracket(start, end, first == '{' && node is not InterpolationSyntax);
            }
        }
    }
}
