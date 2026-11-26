using System.Collections;
using System.Reflection;
using Flow.Model;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.Text;

namespace Flow.Analysis;

internal static class RazorStatementBrackets
{
    // The SDK's public intermediate tree omits @{...} delimiters; only the original Razor syntax tree retains them.
    private const BindingFlags SyntaxMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly PropertyInfo? Root = typeof(RazorSyntaxTree).GetProperty("Root", SyntaxMember);
    private static readonly Type? SyntaxNode = Root?.PropertyType;
    private static readonly MethodInfo? Descendants = SyntaxNode is { } nodeType
        ? nodeType.GetMethod("DescendantNodes", SyntaxMember, [typeof(Func<,>).MakeGenericType(nodeType, typeof(bool))])
        : null;
    private static readonly Type? StatementBody = SyntaxNode?.Assembly
        .GetType("Microsoft.AspNetCore.Razor.Language.Syntax.CSharpStatementBodySyntax");
    private static readonly PropertyInfo? OpenBrace = StatementBody?.GetProperty("OpenBrace", SyntaxMember);
    private static readonly PropertyInfo? CloseBrace = StatementBody?.GetProperty("CloseBrace", SyntaxMember);
    private static readonly PropertyInfo? Span = SyntaxNode?.GetProperty("Span", SyntaxMember);

    internal static IEnumerable<SourceBracket> Read(RazorSyntaxTree tree, string source, CancellationToken token)
    {
        if (SyntaxNode is null || StatementBody is null || OpenBrace is null || CloseBrace is null || Span is null
            || !SyntaxNode.IsAssignableFrom(OpenBrace.PropertyType) || !SyntaxNode.IsAssignableFrom(CloseBrace.PropertyType)
            || Root?.GetValue(tree) is not { } root || Descendants?.Invoke(root, [null]) is not IEnumerable nodes)
            yield break;

        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (!StatementBody.IsInstanceOfType(node) || OpenBrace.GetValue(node) is not { } openBrace
                || CloseBrace.GetValue(node) is not { } closeBrace
                || Span.GetValue(openBrace) is not TextSpan { Length: 1 } open
                || Span.GetValue(closeBrace) is not TextSpan { Length: 1 } close
                || open.Start < 1 || open.Start >= close.Start || close.Start >= source.Length
                || source[open.Start - 1] != '@' || source[open.Start] != '{' || source[close.Start] != '}')
                continue;
            yield return new SourceBracket(open.Start, close.Start, CanFold: true);
        }
    }
}
