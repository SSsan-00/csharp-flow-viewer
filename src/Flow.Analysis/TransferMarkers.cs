using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Flow.Analysis;

// Roslyn models these transfers as regular edges and can merge them into a
// neighboring block. A private analysis-only compilation gives each transfer
// an identifiable CFG operation without moving its evaluation or destination.
internal sealed class TransferMarkers
{
    private readonly record struct Insertion(int OriginalOffset, int NewStart, int Length);
    private readonly Insertion[] insertions;

    private TransferMarkers(SemanticModel model, SyntaxNode declaration, string? markerType, Insertion[] insertions)
    {
        Model = model;
        Declaration = declaration;
        MarkerType = markerType;
        this.insertions = insertions;
    }

    public SemanticModel Model { get; }
    public SyntaxNode Declaration { get; }
    public string? MarkerType { get; }

    public static TransferMarkers Create(SemanticModel model, SyntaxNode declaration, CancellationToken token)
    {
        var statements = declaration.DescendantNodes(node => node == declaration ||
            node is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .OfType<StatementSyntax>().Where(statement => statement is BreakStatementSyntax or ContinueStatementSyntax
                or ReturnStatementSyntax { Expression: null } or ThrowStatementSyntax { Expression: null }).ToArray();
        if (statements.Length == 0)
            return new TransferMarkers(model, declaration, null, []);

        var name = "__FlowViewerTransfer";
        while (model.Compilation.GlobalNamespace.GetMembers(name).Any())
            name += "_";

        var edits = statements.SelectMany(statement => new[]
        {
            (Offset: statement.SpanStart, Text: "{global::" + name + "." + statement.Kind() + "();"),
            (Offset: statement.Span.End, Text: "}")
        }).GroupBy(edit => edit.Offset).OrderBy(group => group.Key)
          .Select(group => new TextChange(new TextSpan(group.Key, 0), string.Concat(group.Select(edit => edit.Text)))).ToArray();

        var total = 0;
        var positions = edits.Select(edit =>
        {
            var insertion = new Insertion(edit.Span.Start, edit.Span.Start + total, edit.NewText!.Length);
            total += insertion.Length;
            return insertion;
        }).ToArray();
        var original = model.SyntaxTree;
        var changed = original.WithChangedText(original.GetText(token).WithChanges(edits));
        var helper = CSharpSyntaxTree.ParseText($$"""
            internal static class {{name}}
            {
                public static void BreakStatement() {}
                public static void ContinueStatement() {}
                public static void ReturnStatement() {}
                public static void ThrowStatement() {}
            }
            """, (CSharpParseOptions)original.Options, cancellationToken: token);
        var compilation = model.Compilation.ReplaceSyntaxTree(original, changed).AddSyntaxTrees(helper);
        var newDeclaration = changed.GetRoot(token).FindToken(declaration.SpanStart).Parent!.AncestorsAndSelf()
            .First(node => node.RawKind == declaration.RawKind && node.SpanStart == declaration.SpanStart);
        return new TransferMarkers(compilation.GetSemanticModel(changed), newDeclaration, name, positions);
    }

    public TextSpan OriginalSpan(TextSpan span) =>
        TextSpan.FromBounds(OriginalOffset(span.Start), OriginalOffset(span.End));

    private int OriginalOffset(int offset)
    {
        var low = 0;
        var high = insertions.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (insertions[mid].NewStart <= offset) low = mid + 1;
            else high = mid;
        }
        if (low == 0) return offset;
        var insertion = insertions[low - 1];
        return Math.Max(insertion.OriginalOffset, offset - (insertion.NewStart - insertion.OriginalOffset) - insertion.Length);
    }
}
