using System.Net;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Flow.Analysis;

internal sealed record InlineScriptText(string Text, string Encoded, int[]? Offsets)
{
    public int EncodedOffset(int offset) => Offsets?[offset] ?? offset;

    public static InlineScriptText? TryDecodeRazorLiteral(string attribute, CancellationToken token)
    {
        var expressionText = attribute.AsSpan().TrimStart();
        if (!expressionText.StartsWith("@(", StringComparison.Ordinal)) return null;
        token.ThrowIfCancellationRequested();
        var expression = SyntaxFactory.ParseExpression(expressionText[1..].ToString());
        if (expression.ContainsDiagnostics || expression is not ParenthesizedExpressionSyntax) return null;
        while (expression is ParenthesizedExpressionSyntax group) expression = group.Expression;
        if (expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            return null;

        var syntax = literal.Token.Text;
        var value = literal.Token.ValueText;
        var start = attribute.Length - expressionText.Length + 1 + literal.Token.SpanStart;
        var offsets = new int[value.Length + 1];
        // Razor already HTML-encodes the C# value, so the browser sees it without an extra entity decode.
        if (literal.Token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken))
        {
            var delimiter = (syntax.Length - value.Length) / 2;
            for (var i = 0; i < offsets.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                offsets[i] = start + delimiter + i;
            }
            return new(value, attribute, offsets);
        }
        if (!literal.Token.IsKind(SyntaxKind.StringLiteralToken)) return null;

        var verbatim = syntax.StartsWith("@\"", StringComparison.Ordinal);
        var end = syntax.Length - 1;
        var position = verbatim ? 2 : 1;
        var written = 0;
        while (position < end)
        {
            token.ThrowIfCancellationRequested();
            var original = position++;
            var width = 1;
            if (verbatim && syntax[original] == '"') position++;
            else if (!verbatim && syntax[original] == '\\')
            {
                switch (syntax[position++])
                {
                    case 'u': position += 4; break;
                    case 'U':
                        if (syntax.AsSpan(position, 4).IndexOfAnyExcept('0') >= 0) width = 2;
                        position += 8;
                        break;
                    case 'x':
                        var limit = Math.Min(end, position + 4);
                        while (position < limit && char.IsAsciiHexDigit(syntax[position])) position++;
                        break;
                }
            }
            if (written + width > value.Length || position > end) return null;
            for (var i = 0; i < width; i++) offsets[written++] = start + original;
        }
        if (written != value.Length) return null;
        offsets[written] = start + end;
        return new(value, attribute, offsets);
    }

    public static InlineScriptText Decode(string encoded, CancellationToken token)
    {
        var decoded = WebUtility.HtmlDecode(encoded);
        if (decoded.Length == encoded.Length) return new(decoded, encoded, null);

        var offsets = new int[decoded.Length + 1];
        int original = 0, projected = 0;
        while (original < encoded.Length)
        {
            token.ThrowIfCancellationRequested();
            if (encoded[original] == '&')
            {
                var tail = encoded.AsSpan(original + 1);
                var terminator = tail.IndexOfAny(';', '&');
                if (terminator >= 0 && tail[terminator] == ';')
                {
                    var entity = encoded.Substring(original, terminator + 2);
                    var value = WebUtility.HtmlDecode(entity);
                    if (value.Length < entity.Length)
                    {
                        for (var i = 0; i < value.Length; i++) offsets[projected++] = original;
                        original += entity.Length;
                        continue;
                    }
                }
            }
            offsets[projected++] = original++;
        }
        offsets[projected] = encoded.Length;
        return new(decoded, encoded, offsets);
    }
}
