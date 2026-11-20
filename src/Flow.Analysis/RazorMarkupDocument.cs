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
    private readonly bool[] dynamicCharacters;
    private readonly List<RazorGuard> guards = [];
    private readonly List<TextSpan> expressions = [];

    public RazorMarkupDocument(string text, string filePath, IReadOnlyList<RazorImport>? imports,
        CancellationToken token)
    {
        source = SourceText.From(text);
        this.filePath = filePath;
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
        var intermediate = document.GetDocumentIntermediateNode();
        Project(intermediate, false);
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
        foreach (var expression in expressions)
        {
            if (expression.Start < offset || expression.End > offset + length) continue;
            var start = expression.Start - offset;
            var available = 0;
            while (available < expression.Length && projection[start + available] is not ('\r' or '\n')) available++;
            var placeholder = available >= 4 ? "null" : "0";
            if (available > 0) placeholder.CopyTo(0, projection, start, placeholder.Length);
        }
        return new string(projection);
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

    private static char[] Blank(string text) => text.Select(character => character is '\r' or '\n' ? character : ' ').ToArray();

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
