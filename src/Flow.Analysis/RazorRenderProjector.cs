using System.Text.RegularExpressions;
using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
                var id = Node("if", NodeKind.Condition, conditional.Condition.ToString(), conditional.Condition);
                scriptSlot ??= document.OriginalSpan(conditional.Span).Start;
                Branch(id, "true", "True", conditional.Statement, next, breakTo, continueTo, scriptSlot.Value, allowConditionalData: true);
                if (conditional.Else is { } alternative)
                    Branch(id, "false", "False", alternative.Statement, next, breakTo, continueTo, scriptSlot.Value, allowConditionalData: true);
                else
                    edges.Add(new FlowEdge(id, next, "False"));
                return id;
            }
            case ForStatementSyntax loop:
                return Loop(loop.Statement, "for", loop.Condition, loop.ForKeyword.Span, counted: loop);
            case WhileStatementSyntax loop:
                return Loop(loop.Statement, "while", loop.Condition, loop.WhileKeyword.Span);
            case DoStatementSyntax loop:
                return Loop(loop.Statement, "do / while", loop.Condition, loop.DoKeyword.Span, firstBody: true);
            case CommonForEachStatementSyntax loop:
                return Loop(loop.Statement, "foreach", loop.Expression, loop.ForEachKeyword.Span);
            case SwitchStatementSyntax choice:
            {
                var id = Node("switch", NodeKind.Condition, $"switch ({choice.Expression})", choice.Expression);
                foreach (var section in choice.Sections)
                {
                    var sectionId = Node("case", NodeKind.Junction, "描画", section);
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

        string Loop(StatementSyntax body, string kind, ExpressionSyntax? condition, TextSpan keyword,
            bool firstBody = false, ForStatementSyntax? counted = null)
        {
            var check = condition?.Span ?? keyword;
            var label = condition is null ? $"{kind}（条件なし）" : kind == "for" ? condition.ToString() : $"{kind} ({condition})";
            var id = NodeAt("loop", NodeKind.Condition, label, check);
            var repeat = counted is null ? id : ProjectForHeader(counted.Incrementors, id, "更新");
            if (condition is not null)
                edges.Add(new FlowEdge(id, next, kind == "foreach" ? "要素なし" : "False"));
            var entry = Branch(id, "body", kind == "foreach" ? "要素あり" : "True",
                body, repeat, next, repeat, scriptSlot ?? document.OriginalSpan(statement.Span).Start, unfoldMixedLoop: true);
            if (firstBody) return entry;
            return counted is null ? id : ProjectForHeader(counted.Declaration is { } declaration
                ? declaration.Variables.Select(variable => variable.Initializer?.Value).OfType<ExpressionSyntax>()
                : counted.Initializers, id, "初期化");
        }

        string Transfer(string label, string target)
        {
            var id = Node("transfer", NodeKind.Transfer, label, statement);
            edges.Add(new FlowEdge(id, target));
            return id;
        }
    }

    private string ProjectForHeader(IEnumerable<ExpressionSyntax> expressions, string next, string phase)
    {
        foreach (var expression in expressions.Reverse())
        {
            token.ThrowIfCancellationRequested();
            if (expression.ContainsDiagnostics) continue;
            var call = expression switch
            {
                InvocationExpressionSyntax direct => direct,
                AssignmentExpressionSyntax { Left: IdentifierNameSyntax, Right: InvocationExpressionSyntax assigned }
                    when expression.IsKind(SyntaxKind.SimpleAssignmentExpression) => assigned,
                _ => null
            };
            string id;
            if (call is not null && !call.DescendantNodes().OfType<InvocationExpressionSyntax>().Any())
                id = Node("for_call", NodeKind.Call, call.ToString(), call);
            else if (expression.DescendantNodesAndSelf(node => node is not AnonymousFunctionExpressionSyntax)
                .OfType<InvocationExpressionSyntax>().Any())
                id = Node("for_header", NodeKind.Transfer, phase, expression);
            else continue;
            edges.Add(new FlowEdge(id, next));
            next = id;
        }
        return next;
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

    private string Branch(string from, string suffix, string outcome, StatementSyntax body,
        string next, string? breakTo, string? continueTo, int scriptSlot, bool unfoldMixedLoop = false, bool allowConditionalData = false)
    {
        var span = document.OriginalSpan(body.Span);
        var id = from + "_" + suffix;
        var skippedOutput = false;
        if (body is BlockSyntax block && !block.OpenBraceToken.IsMissing && !block.CloseBraceToken.IsMissing)
        {
            var interior = document.OriginalSpan(TextSpan.FromBounds(block.OpenBraceToken.Span.End, block.CloseBraceToken.SpanStart));
            var statements = block.Statements.Where(statement => statement is not EmptyStatementSyntax).ToArray();
            if (statements.Length == 0)
                next = OutputNode(interior, next, id, scriptSlot, unfoldMixedLoop, allowConditionalData);
            else
            {
                // Sibling output may alter the JavaScript scope or the statement around a nested control.
                var slot = statements is [var only] && IsControl(only) && IsOnlyOutput(only, interior) ? scriptSlot : (int?)null;
                var end = interior.End;
                for (var index = statements.Length - 1; index >= 0; index--)
                {
                    var statement = statements[index];
                    var child = document.OriginalSpan(statement.Span);
                    next = SplitOutput(TextSpan.FromBounds(child.End, end), next);
                    next = Visit(statement, next, breakTo, continueTo, slot);
                    end = child.Start;
                }
                next = SplitOutput(TextSpan.FromBounds(interior.Start, end), next, prefix: true);
            }
        }
        else next = Visit(body, next, breakTo, continueTo, scriptSlot);
        if (skippedOutput)
        {
            // Preserve the rendering scope for events even when some output nodes hit the display cap.
            nodes.Add(new FlowNode(id, NodeKind.Junction, "描画", document.Range(span.Start, span.Length)));
            bodies.Add((span, id));
            edges.Add(new FlowEdge(id, next));
            next = id;
        }
        edges.Add(new FlowEdge(from, next, outcome));
        return next;

        string SplitOutput(TextSpan part, string continuation, bool prefix = false)
        {
            if (outputNodes < MaxOutputNodes)
                return OutputNode(part, continuation, controlStart: prefix ? scriptSlot : null, splitPrefix: prefix);
            skippedOutput |= document.RenderedOutput(part) is not null;
            return continuation;
        }
    }

    private bool IsOnlyOutput(StatementSyntax statement, TextSpan interior)
    {
        var nested = document.OriginalSpan(statement.Span);
        return interior.Contains(nested)
            && document.RenderedOutput(TextSpan.FromBounds(interior.Start, nested.Start)) is null
            && document.RenderedOutput(TextSpan.FromBounds(nested.End, interior.End)) is null;
    }

    private string OutputNode(TextSpan span, string next, string? id = null, int? controlStart = null,
        bool unfoldMixedLoop = false, bool allowConditionalData = false, bool splitPrefix = false)
    {
        if (document.RenderedOutput(span) is not { } rendered) return next;
        var (range, dynamic) = rendered;
        var splitOutput = id is null;
        id ??= $"render_output{span.Start}";
        var source = document.Range(range.Start, range.Length);
        var scriptOutput = scripts.Any(script => script.Source.Offset <= range.Start
            && range.End <= script.Source.Offset + script.Source.Length);
        var label = scriptOutput ? "生成JS（未確定）" : MarkupLabel(range, dynamic);
        if (scriptOutput && controlStart is int start && outputNodes < MaxOutputNodes
            && (range.Length <= MaxGeneratedScriptLength || dynamic && unfoldMixedLoop)
            && (!dynamic || !splitPrefix && (unfoldMixedLoop || allowConditionalData
                || document.Html.AsSpan(range.Start, range.Length).Contains("bind", StringComparison.Ordinal))))
        {
            var remaining = MaxOutputNodes - outputNodes - (splitOutput ? 1 : 0);
            var hasIterationLineBreak = unfoldMixedLoop
                && (document.HasRenderedLineBreak(span.Start, range.Start) || document.HasRenderedLineBreak(range.End, span.End));
            var branch = dynamic
                ? javaScript.AnalyzeGeneratedBranch(start,
                    document.ProjectJavaScript(range.Start, Math.Min(range.Length, MaxGeneratedScriptLength)), source,
                    remaining, token, document, unfoldMixedLoop, hasIterationLineBreak, allowConditionalData)
                : javaScript.AnalyzeGeneratedBranch(start, document.Html.Substring(range.Start, range.Length), source,
                    remaining, token, hasIterationLineBreak: hasIterationLineBreak, splitPrefix: splitPrefix);
            if (branch?.ClientFlow is { } graph && graph.Nodes.Count <= remaining)
            {
                AddClientAlternative(id, graph);
                if (!dynamic && !graph.IsPartial) label = "生成JS";
            }
            else if (branch is { ClientFlow: null, IsUncertain: false })
                label = "生成JS";
        }
        if (splitOutput) outputNodes++;
        nodes.Add(new FlowNode(id, NodeKind.Junction, label, source));
        bodies.Add((span, id));
        edges.Add(new FlowEdge(id, next));
        return id;
    }

    private string MarkupLabel(TextSpan range, bool dynamic)
    {
        var length = Math.Min(180, range.Length);
        var prefix = dynamic ? document.SourcePreview(range.Start, length) : document.Html.Substring(range.Start, length);
        var preview = Regex.Replace(prefix, @"\s+", " ").Trim();
        if (preview.Length > 90 || range.Length > length)
            preview = preview[..Math.Min(89, preview.Length)] + "…";
        return "描画: " + preview;
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
                ? new FlowEdge(id, prefix + edge.To, "生成JS（候補）")
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
