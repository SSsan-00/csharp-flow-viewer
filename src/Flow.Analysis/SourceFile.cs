using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Flow.Analysis;

public sealed record MethodEntry(int Id, string Name, string Signature, SourceRange Source)
{
    public override string ToString() => $"{Signature}  ·  L{Source.Line}";
}

public sealed class SourceFile
{
    private readonly SemanticModel model;
    private readonly SourceText text;
    private readonly CSharpSyntaxNode root;
    private readonly Dictionary<int, CSharpSyntaxNode> declarations = new();
    private readonly HashSet<int> targets = [];

    public SourceFile(SemanticModel model, IReadOnlyList<string>? notices = null, CancellationToken token = default)
    {
        this.model = model;
        text = model.SyntaxTree.GetText(token);
        root = (CSharpSyntaxNode)model.SyntaxTree.GetRoot(token);
        Notices = notices ?? [];
        var methods = new List<MethodEntry>();
        foreach (var node in root.DescendantNodes().OfType<CSharpSyntaxNode>())
        {
            token.ThrowIfCancellationRequested();
            var symbol = node switch
            {
                BaseMethodDeclarationSyntax method => model.GetDeclaredSymbol(method, token),
                AccessorDeclarationSyntax accessor => model.GetDeclaredSymbol(accessor, token),
                LocalFunctionStatementSyntax local => model.GetDeclaredSymbol(local, token),
                _ => null
            };
            if (symbol is null || symbol.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet)
                continue;

            declarations.Add(node.SpanStart, node);
            if (node is BaseMethodDeclarationSyntax { Body: not null } or BaseMethodDeclarationSyntax { ExpressionBody: not null }
                or AccessorDeclarationSyntax { Body: not null } or AccessorDeclarationSyntax { ExpressionBody: not null }
                or LocalFunctionStatementSyntax { Body: not null } or LocalFunctionStatementSyntax { ExpressionBody: not null })
                targets.Add(node.SpanStart);
            var signature = symbol.ToDisplayString(SignatureFormat);
            methods.Add(new MethodEntry(node.SpanStart, symbol.Name, signature, Range(node.Span)));
        }
        Methods = methods;
    }

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public string Text => text.ToString();
    public IReadOnlyList<MethodEntry> Methods { get; }
    public IReadOnlyList<string> Notices { get; }

    public FlowDiagram Analyze(int methodId, CancellationToken token = default, CallExclusions? excludedCalls = null)
    {
        var node = declarations[methodId];
        try
        {
            var instrument = TransferMarkers.Create(model, node, token);
            var graph = CreateGraph(instrument.Model, instrument.Declaration, token);
            if (graph is null)
                return new FlowDiagram([], [], ["Roslyn CFG を取得できません。抽象メソッド、自動プロパティ、宣言のみのメソッド、または未対応の本体を確認してください。"], true);
            return new DiagramProjector(graph, instrument, Range, targets, token, excludedCalls)
                .Build(Range(node.Span), Notices.Any(notice => notice.StartsWith("解析は部分的", StringComparison.Ordinal)));
        }
        catch (InsufficientExecutionStackException)
        {
            return new FlowDiagram([], [], ["式や制御領域の入れ子が深いため、解析できませんでした。"], true);
        }
    }

    private SourceRange Range(TextSpan span) => new(span.Start, span.Length,
        text.Lines.GetLineFromPosition(span.Start).LineNumber + 1);

    private static ControlFlowGraph? CreateGraph(SemanticModel model, SyntaxNode node, CancellationToken token)
    {
        if (node is LocalFunctionStatementSyntax local)
        {
            if (local.Ancestors().FirstOrDefault(item => item is BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) is not { } parent)
                return null;
            var outer = CreateGraph(model, parent, token);
            var symbol = model.GetDeclaredSymbol(local, token);
            return outer is not null && symbol is not null && outer.LocalFunctions.Contains(symbol, SymbolEqualityComparer.Default)
                ? outer.GetLocalFunctionControlFlowGraph(symbol, token) : null;
        }
        if (node is not (BaseMethodDeclarationSyntax or AccessorDeclarationSyntax))
            return null;
        try { return ControlFlowGraph.Create(node, model, token); }
        catch (ArgumentException) { return null; }
    }
}
