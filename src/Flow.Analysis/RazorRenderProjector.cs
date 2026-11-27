using System.Text.RegularExpressions;
using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Flow.Analysis;

internal sealed class RazorRenderProjector(RazorMarkupDocument document, IReadOnlyList<RazorJavaScriptFragment> scripts,
    RazorJavaScript javaScript, List<FlowNode> nodes,
    List<FlowEdge> edges, CancellationToken token, IReadOnlyList<SourceRange>? excluded = null)
{
    private const string Start = "render_start";
    private const string End = "render_end";
    private const int MaxConditions = 100;
    private readonly List<(TextSpan Body, string Id)> bodies = [];
    private int conditionCount;
    private const int MaxOutputNodes = 200;
    private const int MaxGeneratedScriptLength = 8192;
    private int outputNodes;

    public bool IsTruncated { get; private set; }

    internal static bool IsControl(StatementSyntax statement) => statement is IfStatementSyntax or ForStatementSyntax
        or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax;

    public string? Project(SourceRange pageSource, IReadOnlyList<StatementSyntax>? selectedControls = null,
        string? sharedStart = null)
    {
        var first = selectedControls is null
            ? Visit(document.ServerCode, End, null, null)
            : ProjectSelected(selectedControls);
        if (first == End) return null;
        var start = sharedStart ?? Start;
        if (sharedStart is null)
            nodes.Add(new FlowNode(start, NodeKind.Start, "サーバー側描画", pageSource));
        nodes.Add(new FlowNode(End, NodeKind.End, selectedControls is null ? "描画終了" : "Razor側の生成終了", pageSource));
        edges.Add(new FlowEdge(start, first, sharedStart is null ? null : "サーバー側の生成"));
        MarkDisconnectedPaths(start);
        return start;
    }

    private string ProjectSelected(IReadOnlyList<StatementSyntax> selectedControls)
    {
        var controls = selectedControls.Where(control => !IsExcluded(control.Span)).ToHashSet();
        var first = End;
        for (var i = selectedControls.Count - 1; i >= 0; i--)
        {
            var control = selectedControls[i];
            if (!controls.Contains(control) || control.Ancestors().OfType<StatementSyntax>().Any(controls.Contains))
                continue;
            first = Visit(control, first, null, null);
        }
        return first;
    }

    private bool IsExcluded(TextSpan span)
    {
        if (excluded is not { Count: > 0 }) return false;
        var original = document.OriginalSpan(span);
        return excluded.Any(range => original.Start >= range.Offset && original.End <= range.Offset + range.Length);
    }

    public string? ScopeAt(int offset) => bodies.Where(branch => branch.Body.Contains(offset))
        .OrderBy(branch => branch.Body.Length).Select(branch => branch.Id).FirstOrDefault();

    private string Visit(StatementSyntax statement, string next, string? breakTo, string? continueTo,
        int? scriptSlot = null)
    {
        token.ThrowIfCancellationRequested();
        if (IsExcluded(statement.Span)) return next;
        if (statement is BlockSyntax block) return Sequence(block.Statements, next, breakTo, continueTo);
        if (statement is ReturnStatementSyntax) return Transfer("return", End);
        if (statement is ThrowStatementSyntax) return Transfer("throw", End);
        if (statement is GotoStatementSyntax)
            return Node("boundary", NodeKind.Transfer, "goto の移動先は解析対象外", statement);
        if (statement is TryStatementSyntax)
        {
            var boundary = Node("boundary", NodeKind.Exception, "try / catch の内部フローは解析対象外", statement);
            edges.Add(new FlowEdge(boundary, next, "通常完了した場合"));
            return boundary;
        }
        if (statement is BreakStatementSyntax && breakTo is not null) return Transfer("break", breakTo);
        if (statement is ContinueStatementSyntax && continueTo is not null) return Transfer("continue", continueTo);

        if (!IsControl(statement)) return next;

        if (conditionCount++ >= MaxConditions)
        {
            IsTruncated = true;
            return next;
        }

        switch (statement)
        {
            case IfStatementSyntax conditional:
            {
                var id = Node("if", NodeKind.Condition, $"描画条件: {conditional.Condition}", conditional.Condition);
                scriptSlot ??= document.OriginalSpan(conditional.Span).Start;
                Branch(id, "true", "True", "条件成立時に描画", conditional.Statement, next, breakTo, continueTo, scriptSlot.Value);
                if (conditional.Else is { } alternative)
                    Branch(id, "false", "False", "条件不成立時に描画", alternative.Statement, next, breakTo, continueTo, scriptSlot.Value);
                else
                    edges.Add(new FlowEdge(id, next, "False"));
                return id;
            }
            case ForStatementSyntax loop:
                return Loop(loop.Statement, "for", loop.Condition, loop.ForKeyword.Span);
            case WhileStatementSyntax loop:
                return Loop(loop.Statement, "while", loop.Condition, loop.WhileKeyword.Span);
            case DoStatementSyntax loop:
                return Loop(loop.Statement, "do / while", loop.Condition, loop.DoKeyword.Span, firstBody: true);
            case CommonForEachStatementSyntax loop:
                return Loop(loop.Statement, "foreach", loop.Expression, loop.ForEachKeyword.Span);
            case SwitchStatementSyntax choice:
            {
                var id = Node("switch", NodeKind.Condition, $"描画分岐: switch ({choice.Expression})", choice.Expression);
                foreach (var section in choice.Sections)
                {
                    var sectionId = Node("case", NodeKind.Junction, "選択された分岐を描画", section);
                    bodies.Add((document.OriginalSpan(section.Span), sectionId));
                    edges.Add(new FlowEdge(id, sectionId, string.Join(" / ", section.Labels.Select(label => label.ToString().TrimEnd(':')))));
                    edges.Add(new FlowEdge(sectionId, Sequence(section.Statements, next, next, continueTo)));
                }
                if (!choice.Sections.SelectMany(section => section.Labels).Any(label => label is DefaultSwitchLabelSyntax))
                    edges.Add(new FlowEdge(id, next, "一致なし"));
                return id;
            }
        }

        return next;

        string Loop(StatementSyntax body, string kind, ExpressionSyntax? condition, TextSpan keyword, bool firstBody = false)
        {
            var check = condition?.Span ?? keyword;
            var id = NodeAt("loop", NodeKind.Condition, kind == "foreach" ? $"次の要素: {condition}"
                : $"描画条件: {kind} ({condition?.ToString() ?? "条件省略"})", check);
            var repeatId = kind == "for" ? id : NodeAt("repeat", NodeKind.Junction, "次の反復へ", keyword);
            if (repeatId != id) edges.Add(new FlowEdge(repeatId, id));
            if (condition is not null)
                edges.Add(new FlowEdge(id, next, kind == "foreach" ? "要素なし" : "False"));
            var entry = Branch(id, "body", kind == "foreach" ? "要素あり" : "True", "ループ本体を描画",
                body, repeatId, next, repeatId, scriptSlot ?? document.OriginalSpan(statement.Span).Start, unfoldMixedLoop: true);
            return firstBody ? entry : id;
        }

        string Transfer(string label, string target)
        {
            var id = Node("transfer", NodeKind.Transfer, label, statement);
            edges.Add(new FlowEdge(id, target));
            return id;
        }
    }

    private void MarkDisconnectedPaths(string start)
    {
        var outgoing = edges.ToLookup(edge => edge.From, edge => edge.To);
        var reachable = new HashSet<string>();
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.TryPop(out var id))
        {
            token.ThrowIfCancellationRequested();
            if (!reachable.Add(id)) continue;
            foreach (var next in outgoing[id]) pending.Push(next);
        }
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (node.Id.StartsWith("render_", StringComparison.Ordinal) && !reachable.Contains(node.Id))
                nodes[index] = node with { IsReachable = false };
        }
        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            if (edge.From.StartsWith("render_", StringComparison.Ordinal) && !reachable.Contains(edge.From))
                edges[index] = edge with { IsReachable = false };
        }
    }

    private string Sequence(SyntaxList<StatementSyntax> statements, string next, string? breakTo, string? continueTo)
    {
        for (var index = statements.Count - 1; index >= 0; index--)
            next = Visit(statements[index], next, breakTo, continueTo);
        return next;
    }

    private string Branch(string from, string suffix, string outcome, string label, StatementSyntax body,
        string next, string? breakTo, string? continueTo, int scriptSlot, bool unfoldMixedLoop = false)
    {
        var span = document.OriginalSpan(body.Span);
        var id = from + "_" + suffix;
        var source = document.Range(span.Start, span.Length);
        if (body is BlockSyntax block && !block.OpenBraceToken.IsMissing && !block.CloseBraceToken.IsMissing)
        {
            var interior = document.OriginalSpan(TextSpan.FromBounds(block.OpenBraceToken.Span.End, block.CloseBraceToken.SpanStart));
            var statements = block.Statements.Where(statement => statement is not EmptyStatementSyntax).ToArray();
            if (statements.Length == 0)
                DescribeOutput(interior, id, ref label, ref source, scriptSlot, unfoldMixedLoop);
            else
            {
                // Sibling output may alter the JavaScript scope or the statement around a nested control.
                var slot = statements is [var only] && IsControl(only) && IsOnlyOutput(only, interior) ? scriptSlot : (int?)null;
                var end = interior.End;
                for (var index = statements.Length - 1; index >= 0; index--)
                {
                    var statement = statements[index];
                    var child = document.OriginalSpan(statement.Span);
                    next = OutputNode(TextSpan.FromBounds(child.End, end), next);
                    next = Visit(statement, next, breakTo, continueTo, slot);
                    end = child.Start;
                }
                next = OutputNode(TextSpan.FromBounds(interior.Start, end), next);
            }
        }
        else next = Visit(body, next, breakTo, continueTo, scriptSlot);
        nodes.Add(new FlowNode(id, NodeKind.Junction, label, source));
        bodies.Add((span, id));
        edges.Add(new FlowEdge(from, id, outcome));
        edges.Add(new FlowEdge(id, next));
        return id;
    }

    private bool IsOnlyOutput(StatementSyntax statement, TextSpan interior)
    {
        var nested = document.OriginalSpan(statement.Span);
        return interior.Contains(nested)
            && document.RenderedOutput(TextSpan.FromBounds(interior.Start, nested.Start)) is null
            && document.RenderedOutput(TextSpan.FromBounds(nested.End, interior.End)) is null;
    }

    private string OutputNode(TextSpan span, string next)
    {
        if (outputNodes >= MaxOutputNodes) return next;
        var id = $"render_output{span.Start}";
        var label = "描画";
        var source = document.Range(span.Start, span.Length);
        if (!DescribeOutput(span, id, ref label, ref source)) return next;
        outputNodes++;
        nodes.Add(new FlowNode(id, NodeKind.Transfer, label, source));
        edges.Add(new FlowEdge(id, next));
        return id;
    }

    private bool DescribeOutput(TextSpan span, string id, ref string label, ref SourceRange source, int? controlStart = null,
        bool unfoldMixedLoop = false)
    {
        if (document.RenderedOutput(span) is not { } rendered) return false;
        var (range, dynamic) = rendered;
        var (offset, end) = (range.Start, range.End);
        var length = range.Length;
        var output = document.Html.AsSpan(offset, length);
        var script = scripts.Any(script => script.Source.Offset <= offset && end <= script.Source.Offset + script.Source.Length);
        source = document.Range(offset, length);
        if (script) label += " · 生成JS";
        string? analyzedPreview = null;
        if (script && controlStart is int start && outputNodes < MaxOutputNodes
            && (length <= MaxGeneratedScriptLength || dynamic && unfoldMixedLoop)
            && (!dynamic || unfoldMixedLoop || output.Contains("bind", StringComparison.Ordinal)))
        {
            var remaining = MaxOutputNodes - outputNodes;
            var hasIterationLineBreak = unfoldMixedLoop
                && (document.HasRenderedLineBreak(span.Start, offset) || document.HasRenderedLineBreak(end, span.End));
            var branch = dynamic
                ? javaScript.AnalyzeGeneratedBranch(start,
                    document.ProjectJavaScript(offset, Math.Min(length, MaxGeneratedScriptLength)), source, remaining,
                    token, document, unfoldMixedLoop, hasIterationLineBreak)
                : javaScript.AnalyzeGeneratedBranch(start, output.ToString(), source, remaining, token);
            analyzedPreview = branch?.Preview;
            if (branch?.ClientFlow is { } graph && graph.Nodes.Count <= remaining)
            {
                AddClientAlternative(id, graph);
                if (dynamic && unfoldMixedLoop)
                {
                    label += "（一部未確定）";
                    return true;
                }
            }
        }
        if (script && dynamic && unfoldMixedLoop)
        {
            label += "（詳細未確定）";
            return true;
        }
        if (analyzedPreview == "") return true;
        var prefix = analyzedPreview ?? (dynamic ? document.SourcePreview(offset, Math.Min(180, length))
            : output[..Math.Min(180, length)].ToString());
        var preview = Regex.Replace(prefix, @"\s+", " ").Trim();
        if (preview.Length > 90 || analyzedPreview is null && length > 180)
            preview = preview[..Math.Min(89, preview.Length)] + "…";
        label += (dynamic ? "（Razor式を含む原文）" : "") + ": " + preview;
        return true;
    }

    private void AddClientAlternative(string id, FlowDiagram graph)
    {
        var start = graph.Nodes.Single(node => node.Kind == NodeKind.Start);
        var prefix = id + "_candidate_";
        outputNodes += graph.Nodes.Count;
        foreach (var node in graph.Nodes.Where(node => node != start))
            nodes.Add(node with { Id = prefix + node.Id, Label = node.Kind == NodeKind.End ? "JS断片の終了" : node.Label });
        foreach (var edge in graph.Edges)
            edges.Add(edge.From == start.Id
                ? new FlowEdge(id, prefix + edge.To, "生成後、このJSが実行される場合")
                : edge with { From = prefix + edge.From, To = prefix + edge.To });
    }

    private string Node(string prefix, NodeKind kind, string label, SyntaxNode node) =>
        NodeAt(prefix, kind, label, node.Span);

    private string NodeAt(string prefix, NodeKind kind, string label, TextSpan range)
    {
        var span = document.OriginalSpan(range);
        var id = $"render_{prefix}{span.Start}";
        nodes.Add(new FlowNode(id, kind, label, document.Range(span.Start, span.Length)));
        return id;
    }
}
