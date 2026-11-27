using System.Collections.Immutable;
using System.Text;
using Flow.Model;
using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Flow.Analysis;

/// <summary>A Razor import keeps its own path so imported directives are resolved in context.</summary>
public sealed record RazorImport(string Path, string Text);

/// <summary>
/// Uses the SDK's public Razor intermediate representation to distinguish markup from
/// server code. Both projections retain the original length and line endings.
/// </summary>
internal sealed class RazorMarkupDocument
{
    private readonly SourceText source;
    private readonly string filePath;
    private readonly bool collectSourceBrackets;
    private readonly bool[] dynamicCharacters;
    private readonly SourceBracket[] statementBrackets;
    private readonly List<RazorGuard> guards = [];
    private readonly List<TextSpan> expressions = [];
    private readonly List<int> renderedLineBreaks = [];
    private static readonly IComparer<TextSpan> ByStart = Comparer<TextSpan>.Create((a, b) => a.Start.CompareTo(b.Start));
    private readonly (TextSpan Span, StatementSyntax Statement)[] renderControls;

    public RazorMarkupDocument(string text, string filePath, IReadOnlyList<RazorImport>? imports,
        CancellationToken token, bool collectSourceBrackets)
    {
        source = SourceText.From(text);
        this.filePath = filePath;
        this.collectSourceBrackets = collectSourceBrackets;
        dynamicCharacters = new bool[text.Length];
        var markup = Blank(text);
        var code = Blank(text);
        var directives = new DirectiveCapturePass();
        var engine = RazorProjectEngine.Create(RazorConfiguration.Default,
            RazorProjectFileSystem.Create(Path.GetDirectoryName(Path.GetFullPath(filePath))!), builder =>
            {
                RazorExtensions.Register(builder);
                builder.Features.Add(directives);
            });
        token.ThrowIfCancellationRequested();
        // ReadFrom(ProjectItem) supplies RelativePath as well as FilePath. Without
        // RelativePath Razor silently falls back to its generated namespace even
        // when a page or an imported file contains an explicit @namespace.
        var document = engine.Process(RazorSourceDocument.ReadFrom(new MemoryProjectItem(text, filePath)), FileKinds.Legacy,
            (imports ?? []).Select(import => RazorSourceDocument.ReadFrom(new MemoryProjectItem(import.Text, import.Path))).ToImmutableArray(), []);
        token.ThrowIfCancellationRequested();
        statementBrackets = collectSourceBrackets && text.Contains("@{", StringComparison.Ordinal)
            ? RazorStatementBrackets.Read(document.GetSyntaxTree(), text, token).ToArray() : [];
        var intermediate = document.GetDocumentIntermediateNode();
        Project(intermediate, false);
        expressions.Sort(ByStart);
        renderedLineBreaks.Sort();
        Html = new string(markup);
        ModelName = directives.ModelName;
        IsPage = directives.IsPage;
        RouteTemplate = directives.RouteTemplate;
        TagHelperNames = directives.TagHelperNames;
        TagHelperPrefix = directives.TagHelperPrefix;
        NamespaceName = Descendants(intermediate).OfType<NamespaceDeclarationIntermediateNode>()
            .FirstOrDefault()?.Content;
        Usings = Descendants(intermediate).OfType<UsingDirectiveIntermediateNode>()
            .Select(node => node.Content).Distinct(StringComparer.Ordinal).ToArray();
        Notices = document.GetCSharpDocument().Diagnostics
            .Select(diagnostic => $"Razor · {diagnostic.Id} · L{diagnostic.Span.LineIndex + 1}: {diagnostic.GetMessage()}")
            .Distinct(StringComparer.Ordinal).ToArray();
        ServerCode = (BlockSyntax)SyntaxFactory.ParseStatement("{" + new string(code) + "}");
        renderControls = ServerCode.DescendantNodes(static node =>
                node is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .OfType<StatementSyntax>().Where(statement => RazorRenderProjector.IsControl(statement)
                || statement is TryStatementSyntax).Select(statement => (OriginalSpan(statement.Span), statement)).ToArray();
        BuildGuards();

        void Project(IntermediateNode node, bool htmlContext)
        {
            token.ThrowIfCancellationRequested();
            // Attribute wrappers contain the name, equals sign and quotes. Child C#
            // values are erased below so quotes inside Razor expressions cannot end them.
            if (node is HtmlAttributeIntermediateNode && OwnSpan(node.Source, out var attributeSpan))
                Copy(text, markup, attributeSpan);

            var isCode = node is CSharpCodeIntermediateNode or CSharpExpressionIntermediateNode
                or CSharpCodeAttributeValueIntermediateNode or CSharpExpressionAttributeValueIntermediateNode;
            if (isCode && OwnSpan(node.Source, out var codeSpan))
            {
                if (node is CSharpExpressionIntermediateNode or CSharpExpressionAttributeValueIntermediateNode)
                    expressions.Add(codeSpan);
                for (var i = codeSpan.Start; i < codeSpan.End; i++)
                {
                    if (!char.IsWhiteSpace(text[i])) dynamicCharacters[i] = true;
                    markup[i] = text[i] is '\r' or '\n' ? text[i] : ' ';
                }
                if (node is CSharpCodeIntermediateNode) Copy(text, code, codeSpan);
            }
            var childHtmlContext = !isCode && (htmlContext || node is HtmlContentIntermediateNode
                or HtmlAttributeValueIntermediateNode or HtmlAttributeIntermediateNode);
            if (node is IntermediateToken && childHtmlContext && OwnSpan(node.Source, out var htmlSpan))
            {
                Copy(text, markup, htmlSpan);
                var remaining = text.AsSpan(htmlSpan.Start, htmlSpan.Length);
                while (remaining.IndexOfAny('\r', '\n') is var lineBreak && lineBreak >= 0)
                {
                    remaining = remaining[(lineBreak + 1)..];
                    renderedLineBreaks.Add(htmlSpan.End - remaining.Length - 1);
                }
                // Empty statements make HTML locations belong to their surrounding
                // if/else/loop body when Roslyn reads the server-code projection.
                if (htmlSpan.Length > 0 && !char.IsWhiteSpace(text[htmlSpan.Start])) code[htmlSpan.Start] = ';';
            }
            foreach (var child in node.Children) Project(child, childHtmlContext);
        }
    }

    public string Html { get; }
    public BlockSyntax ServerCode { get; }
    public string? ModelName { get; }
    public bool IsPage { get; }
    public string? RouteTemplate { get; }
    public string? NamespaceName { get; }
    public IReadOnlyList<string> Usings { get; }
    public IReadOnlyList<string> Notices { get; }
    public IReadOnlySet<string> TagHelperNames { get; }
    public string? TagHelperPrefix { get; }

    public bool HasTagHelper(string name) => string.IsNullOrEmpty(TagHelperPrefix) && TagHelperNames.Contains(name);

    public SourceRange Range(int offset, int length) => new(offset, length,
        source.Lines.GetLineFromPosition(Math.Min(offset, source.Length)).LineNumber + 1, filePath);

    internal IEnumerable<SourceBracket> Brackets(CancellationToken token)
    {
        foreach (var pair in statementBrackets) yield return pair;
        foreach (var pair in CSharpSourceBrackets.Read(ServerCode, source, -1, token)) yield return pair;
        foreach (var expression in expressions)
        {
            token.ThrowIfCancellationRequested();
            var syntax = SyntaxFactory.ParseExpression(source.ToString(expression));
            if (syntax.ContainsDiagnostics) continue;
            foreach (var pair in CSharpSourceBrackets.Read(syntax, source, expression.Start, token)) yield return pair;
            if (expression.Start >= 2 && expression.End < source.Length && source[expression.Start - 2] == '@'
                && source[expression.Start - 1] == '(' && source[expression.End] == ')')
                yield return new SourceBracket(expression.Start - 1, expression.End);
        }
    }

    internal (TextSpan Span, bool Dynamic)? RenderedOutput(TextSpan scope)
    {
        var html = Html.AsSpan(scope.Start, scope.Length);
        var start = scope.Start + html.Length - html.TrimStart().Length;
        var end = scope.Start + html.TrimEnd().Length;
        var dynamic = IsDynamic(scope.Start, scope.Length);
        if (dynamic)
        {
            var index = expressions.BinarySearch(scope, ByStart);
            for (var i = index < 0 ? ~index : index; i < expressions.Count && expressions[i].Start < scope.End; i++)
            {
                var expression = expressions[i];
                if (!scope.Contains(expression)) continue;
                var at = expression.Start > scope.Start && source[expression.Start - 1] == '@';
                start = Math.Min(start, expression.Start - (at ? 1 : 0));
                end = Math.Max(end, expression.End);
            }
        }
        return start >= end ? null : (TextSpan.FromBounds(start, end), dynamic);
    }

    internal string SourcePreview(int offset, int length) => source.ToString(new TextSpan(offset, length));

    internal bool HasRenderedLineBreak(int start, int end)
    {
        var index = renderedLineBreaks.BinarySearch(start);
        if (index < 0) index = ~index;
        return index < renderedLineBreaks.Count && renderedLineBreaks[index] < end;
    }

    internal bool HasOnlyQuotedReferences(SourceRange output, SourceRange literal)
    {
        if (literal.Length < 3) return false;
        var start = output.Offset;
        var end = start + output.Length;
        var insideQuotes = new TextSpan(literal.Offset + 1, literal.Length - 2);
        var cursor = start;
        var search = expressions.BinarySearch(new TextSpan(start, output.Length), ByStart);
        for (var i = search < 0 ? ~search : search; i < expressions.Count && expressions[i].Start < end; i++)
        {
            var expression = expressions[i];
            if (!insideQuotes.Contains(expression) || expression.Start < cursor
                || IsDynamic(cursor, expression.Start - cursor) || !IsSimpleReference(expression))
                return false;
            cursor = expression.End;
        }
        return cursor > start && !IsDynamic(cursor, end - cursor);

        bool IsSimpleReference(TextSpan expression)
        {
            var syntax = SyntaxFactory.ParseExpression(source.ToString(expression));
            if (syntax.ContainsDiagnostics) return false;
            while (true)
            {
                switch (syntax)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        syntax = parenthesized.Expression;
                        break;
                    case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax } member when member.RawKind == (int)SyntaxKind.SimpleMemberAccessExpression:
                        syntax = member.Expression;
                        break;
                    default:
                        return syntax is IdentifierNameSyntax;
                }
            }
        }
    }

    internal SourceRange CoverGeneratedExpressions(SourceRange range)
    {
        var span = new TextSpan(range.Offset, range.Length);
        var search = expressions.BinarySearch(span, ByStart);
        var start = span.Start;
        var end = span.End;
        for (var i = Math.Max(0, (search < 0 ? ~search : search) - 1);
             i < expressions.Count && expressions[i].Start < span.End; i++)
        {
            var expression = expressions[i];
            if (!span.OverlapsWith(expression)) continue;
            var transition = expression.Start > 0 && source[expression.Start - 1] == '@' ? 1 : 0;
            start = Math.Min(start, expression.Start - transition);
            end = Math.Max(end, expression.End);
        }
        return Range(start, end - start);
    }

    public bool IsDynamic(int offset, int length)
    {
        var end = Math.Min(offset + length, dynamicCharacters.Length);
        for (var index = Math.Max(offset, 0); index < end; index++)
            if (dynamicCharacters[index]) return true;
        return false;
    }

    /// <summary>
    /// Preserve literal JavaScript while substituting an unknown expression value.
    /// The caller keeps ContainsRazorCode set: the placeholder is parsing scaffolding,
    /// never evidence that the runtime value is null or that both Razor branches run.
    /// </summary>
    public string ProjectJavaScript(int offset, int length)
    {
        var projection = Html.Substring(offset, length).ToCharArray();
        var range = new TextSpan(offset, length);
        var index = expressions.BinarySearch(range, ByStart);
        if (index < 0) index = ~index;
        else while (index > 0 && expressions[index - 1].Start == offset) index--;
        for (var i = index; i < expressions.Count && expressions[i].Start < range.End; i++)
        {
            var expression = expressions[i];
            if (!range.Contains(expression)) continue;
            var start = expression.Start - offset;
            var available = 0;
            while (available < expression.Length && projection[start + available] is not ('\r' or '\n')) available++;
            var placeholder = available >= 4 ? "null" : "0";
            if (available > 0) placeholder.CopyTo(0, projection, start, placeholder.Length);
        }
        return new string(projection);
    }

    internal RazorJavaScriptFragment ProjectScript(int offset, int length, bool isModule, CancellationToken token)
    {
        var range = Range(offset, length);
        var text = ProjectJavaScript(offset, length);
        var controls = ControlsWithin(range);
        if (controls.Count == 0)
            return new RazorJavaScriptFragment(text, range, IsDynamic(offset, length), isModule);

        var projected = text.ToCharArray();
        var slots = new Dictionary<int, SourceRange>();
        var generated = new List<(int Slot, SourceRange Source, string Text)>();
        var previousEnd = offset;
        foreach (var control in controls)
        {
            token.ThrowIfCancellationRequested();
            var span = OriginalSpan(control.Span);
            if (span.Start < previousEnd) continue;
            for (var i = span.Start; i < span.End; i++)
                if (projected[i - offset] is not ('\r' or '\n')) projected[i - offset] = ' ';
            // Leave a statement-sized boundary, not a made-up JavaScript value or both branches.
            projected[span.Start - offset] = ';';
            slots.Add(span.Start - offset, Range(span.Start, span.Length));
            previousEnd = span.End;
            if (!collectSourceBrackets) continue;
            foreach (var node in control.DescendantNodes())
            {
                token.ThrowIfCancellationRequested();
                if (node is BlockSyntax block && !block.OpenBraceToken.IsMissing && !block.CloseBraceToken.IsMissing)
                    AddGeneratedOutput(span.Start, block.Statements, block.OpenBraceToken.Span.End, block.CloseBraceToken.SpanStart);
                else if (node is SwitchSectionSyntax section && section.Labels.Count > 0)
                    AddGeneratedOutput(span.Start, section.Statements, section.Labels[^1].Span.End, section.Span.End);
            }
        }

        return new RazorJavaScriptFragment(new string(projected), range, true, isModule)
        {
            RenderingSlots = slots,
            GeneratedStatements = generated
        };

        void AddGeneratedOutput(int slot, Microsoft.CodeAnalysis.SyntaxList<StatementSyntax> statements, int start, int end)
        {
            foreach (var statement in statements)
            {
                if (statement is EmptyStatementSyntax) continue;
                Add(start, statement.SpanStart);
                start = statement.Span.End;
            }
            Add(start, end);

            void Add(int first, int last)
            {
                if (first >= last || RenderedOutput(OriginalSpan(TextSpan.FromBounds(first, last))) is not { } output)
                    return;
                var body = output.Span;
                var text = output.Dynamic ? ProjectJavaScript(body.Start, body.Length) : Html.Substring(body.Start, body.Length);
                generated.Add((slot, Range(body.Start, body.Length), text));
            }
        }
    }

    internal IReadOnlyList<StatementSyntax> ControlsWithin(SourceRange range)
    {
        var selected = new TextSpan(range.Offset, range.Length);
        var first = 0;
        var last = renderControls.Length;
        while (first < last)
        {
            var middle = first + (last - first) / 2;
            if (renderControls[middle].Span.Start < selected.Start) first = middle + 1;
            else last = middle;
        }
        List<StatementSyntax>? matches = null;
        for (var i = first; i < renderControls.Length && renderControls[i].Span.Start <= selected.End; i++)
        {
            var (span, statement) = renderControls[i];
            if (selected.Contains(span)) (matches ??= []).Add(statement);
        }
        return matches ?? [];
    }

    public IReadOnlyList<RazorGuard> GuardsAt(int offset) => guards
        .Where(guard => guard.Body.Contains(offset)).OrderByDescending(guard => guard.Body.Length).ToArray();

    private bool OwnSpan(SourceSpan? span, out TextSpan result)
    {
        result = default;
        if (span is not { } value || value.AbsoluteIndex < 0 || value.Length <= 0
            || value.AbsoluteIndex + value.Length > source.Length
            || !string.Equals(value.FilePath, filePath, StringComparison.Ordinal)) return false;
        result = new TextSpan(value.AbsoluteIndex, value.Length);
        return true;
    }

    private void BuildGuards()
    {
        foreach (var node in ServerCode.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax conditional:
                    Add(conditional.Statement, conditional.Condition, conditional.Condition.ToString());
                    if (conditional.Else is { } alternative)
                        Add(alternative.Statement, conditional.Condition, $"!({conditional.Condition})");
                    break;
                case ForEachStatementSyntax loop:
                    Add(loop.Statement, loop.Expression, $"foreach ({loop.Identifier} in {loop.Expression})");
                    break;
                case ForStatementSyntax loop:
                    Add(loop.Statement, loop.Condition ?? (Microsoft.CodeAnalysis.SyntaxNode)loop,
                        $"for ({loop.Condition?.ToString() ?? "条件省略"})");
                    break;
                case WhileStatementSyntax loop:
                    Add(loop.Statement, loop.Condition, $"while ({loop.Condition})");
                    break;
            }
        }

        void Add(StatementSyntax body, Microsoft.CodeAnalysis.SyntaxNode condition, string label)
        {
            var bodySpan = OriginalSpan(body.Span);
            var conditionSpan = OriginalSpan(condition.Span);
            if (bodySpan.Length == 0 || conditionSpan.Length == 0) return;
            guards.Add(new RazorGuard(label, Range(conditionSpan.Start, conditionSpan.Length), bodySpan));
        }
    }

    // ServerCode has an artificial leading brace; every other offset is preserved.
    internal TextSpan OriginalSpan(TextSpan value)
    {
        var start = Math.Clamp(value.Start - 1, 0, source.Length);
        var end = Math.Clamp(value.End - 1, start, source.Length);
        return TextSpan.FromBounds(start, end);
    }

    private static char[] Blank(string text)
    {
        var blank = text.ToCharArray();
        for (var i = 0; i < blank.Length; i++)
            if (blank[i] is not ('\r' or '\n')) blank[i] = ' ';
        return blank;
    }

    private static void Copy(string text, char[] output, TextSpan span) => text.CopyTo(span.Start, output, span.Start, span.Length);

    private static IEnumerable<IntermediateNode> Descendants(IntermediateNode root)
    {
        var pending = new Stack<IntermediateNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--) pending.Push(node.Children[index]);
        }
    }

    /// <summary>Capture directives before the compiler removes them during optimization.</summary>
    private sealed class DirectiveCapturePass : IntermediateNodePassBase, IRazorDirectiveClassifierPass
    {
        public override int Order => int.MinValue;
        public string? ModelName { get; private set; }
        public bool IsPage { get; private set; }
        public string? RouteTemplate { get; private set; }
        public HashSet<string> TagHelperNames { get; } = new(StringComparer.Ordinal);
        public string? TagHelperPrefix { get; private set; }

        protected override void ExecuteCore(RazorCodeDocument codeDocument, DocumentIntermediateNode documentNode)
        {
            if (Descendants(documentNode).OfType<DirectiveIntermediateNode>().Any(node => node.DirectiveName == "model"))
                ModelName = ModelDirective.GetModelType(documentNode);
            IsPage = PageDirective.TryGetPageDirective(documentNode, out var page);
            RouteTemplate = IsPage ? page.RouteTemplate : null;
            foreach (var directive in Descendants(documentNode).OfType<DirectiveIntermediateNode>())
            {
                var value = string.Join(" ", directive.Tokens.Select(token => token.Content)).Trim().Trim('"', '\'');
                if (directive.DirectiveName == "tagHelperPrefix") TagHelperPrefix = value;
                if (directive.DirectiveName is not ("addTagHelper" or "removeTagHelper")) continue;
                var parts = value.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[1] != "Microsoft.AspNetCore.Mvc.TagHelpers") continue;
                foreach (var name in new[] { "FormTagHelper", "FormActionTagHelper", "AnchorTagHelper" })
                {
                    var fullName = "Microsoft.AspNetCore.Mvc.TagHelpers." + name;
                    var matches = parts[0] == fullName || parts[0] == "*"
                        || parts[0].EndsWith('*') && fullName.StartsWith(parts[0][..^1], StringComparison.Ordinal);
                    if (!matches) continue;
                    if (directive.DirectiveName == "addTagHelper") TagHelperNames.Add(name);
                    else TagHelperNames.Remove(name);
                }
            }
        }
    }

    /// <summary>Keep import paths and public compiler namespace resolution without touching disk.</summary>
    private sealed class MemoryProjectItem(string text, string path) : RazorProjectItem
    {
        public override string BasePath => "/";
        public override string FilePath => "/" + Path.GetFileName(path);
        public override string PhysicalPath => path;
        public override string RelativePhysicalPath => Path.GetFileName(path);
        public override bool Exists => true;
        public override Stream Read() => new MemoryStream(Encoding.UTF8.GetBytes(text));
    }
}

internal sealed record RazorGuard(string Label, SourceRange Source, TextSpan Body);
