using Acornima;
using Acornima.Ast;
using Flow.Model;
using JsProgram = Acornima.Ast.Program;

namespace Flow.Analysis;

/// <summary>
/// JavaScriptの選択した本体を図へ投影する。関数宣言の本体や非同期コールバックは
/// 呼び出し位置で実行されたものとして展開せず、別エントリーへの移動先にする。
/// </summary>
internal sealed class RazorJavaScriptProjector(RazorJavaScript owner, RazorJavaScript.EntryData entry,
    CancellationToken token)
{
    private readonly List<FlowNode> nodes = [];
    private readonly List<FlowEdge> edges = [];
    private readonly List<string> notices = [];
    private string end = "";
    private int nextId;
    private int depth;
    private bool partial;

    public FlowDiagram Build()
    {
        end = Add(NodeKind.End, "JavaScript 終了", entry.Body);
        if (entry.Unit.ContainsRazor)
            MarkPartial("Razorが生成するJavaScriptの値と実行経路は確定できません。この図は静的に抽出できた処理の候補を表示します。");
        if (entry.Body is null)
        {
            var unavailable = Add(NodeKind.Exception, entry.Failure ?? "JavaScript解析未解決", null);
            Connect(unavailable, end);
            var start = Add(NodeKind.Start, entry.Entry.Signature, null);
            Connect(start, unavailable);
            MarkPartial(entry.Failure ?? "JavaScript解析未解決");
        }
        else
        {
            string first;
            try
            {
                if (entry.Body is Expression expression)
                {
                    var returning = Add(NodeKind.Transfer, "return " + Text(expression), expression);
                    Connect(returning, end);
                    first = Expression(expression, returning);
                }
                else first = Statement(entry.Body, end, null, null);
            }
            catch (AnalysisLimitException)
            {
                MarkPartial("JavaScriptの図が大きいため、詳細の展開を中断しました。");
                first = Add(NodeKind.Exception, "解析範囲の上限", null, ignoreLimit: true);
                Connect(first, end);
            }
            var start = Add(NodeKind.Start, entry.Entry.Signature, entry.Body, ignoreLimit: true);
            Connect(start, first);
        }
        return new FlowDiagram(nodes, edges, notices, partial);
    }

    private string Statement(Node statement, string next, string? breakTarget, string? continueTarget)
    {
        token.ThrowIfCancellationRequested();
        if (++depth > 128) throw new AnalysisLimitException();
        try
        {
            switch (statement)
            {
                case JsProgram program:
                    return Sequence(program.Body.ToArray(), next, breakTarget, continueTarget);
                case BlockStatement block:
                    return Sequence(block.Body.ToArray(), next, breakTarget, continueTarget);
                case EmptyStatement when entry.Unit.RenderingSlots?.TryGetValue(statement.Start, out var slot) == true:
                    var boundary = Add(NodeKind.Exception, "Razorが生成するJavaScript · 実行経路未解決", statement,
                        source: slot);
                    Connect(boundary, next, "続行する場合");
                    return boundary;
                case FunctionDeclaration:
                case EmptyStatement:
                    return next;
                case VariableDeclaration declaration:
                    var after = next;
                    for (var i = declaration.Declarations.Count - 1; i >= 0; --i)
                    {
                        var variable = declaration.Declarations[i];
                        if (variable.Init is null or IFunction) continue;
                        var set = Add(NodeKind.Transfer, Text(variable), variable);
                        Connect(set, after);
                        after = Expression(variable.Init, set);
                    }
                    return after;
                case ExpressionStatement expression:
                    return Expression(expression.Expression, next);
                case IfStatement conditional:
                    return Condition(conditional.Test,
                        Statement(conditional.Consequent, next, breakTarget, continueTarget),
                        conditional.Alternate is { } alternate ? Statement(alternate, next, breakTarget, continueTarget) : next);
                case ReturnStatement returning:
                    var returnNode = Add(NodeKind.Transfer, returning.Argument is BooleanLiteral { Value: false }
                        ? "return false（登録方法により既定動作を中止）" : Text(returning), returning);
                    Connect(returnNode, end);
                    return returning.Argument is { } value ? Expression(value, returnNode) : returnNode;
                case ThrowStatement throwing:
                    var throwNode = Add(NodeKind.Exception, Text(throwing), throwing);
                    MarkPartial("JavaScriptの例外伝播先はこの図では解決しません。");
                    Connect(throwNode, end, "例外");
                    return Expression(throwing.Argument, throwNode);
                case BreakStatement { Label: null }:
                    var breaking = Add(NodeKind.Transfer, "break", statement);
                    Connect(breaking, breakTarget ?? end);
                    if (breakTarget is null) MarkPartial("breakの移動先を解決できません。");
                    return breaking;
                case ContinueStatement { Label: null }:
                    var continuing = Add(NodeKind.Transfer, "continue", statement);
                    Connect(continuing, continueTarget ?? end);
                    if (continueTarget is null) MarkPartial("continueの移動先を解決できません。");
                    return continuing;
                case WhileStatement loop:
                    var whileAnchor = Add(NodeKind.Junction, "while 判定へ", loop.Test);
                    var whileBody = Statement(loop.Body, whileAnchor, next, whileAnchor);
                    Connect(whileAnchor, Condition(loop.Test, whileBody, next));
                    return whileAnchor;
                case DoWhileStatement loop:
                    var doAnchor = Add(NodeKind.Junction, "do / while 判定へ", loop.Test);
                    var doBody = Statement(loop.Body, doAnchor, next, doAnchor);
                    Connect(doAnchor, Condition(loop.Test, doBody, next));
                    return doBody;
                case ForStatement loop:
                    var forAnchor = Add(NodeKind.Junction, "for 判定へ", loop.Test ?? (Node)loop);
                    var update = loop.Update is { } increment ? Expression(increment, forAnchor) : forAnchor;
                    var forBody = Statement(loop.Body, update, next, update);
                    Connect(forAnchor, loop.Test is { } test ? Condition(test, forBody, next) : forBody);
                    return loop.Init is { } initialize ? Statement(initialize, forAnchor, breakTarget, continueTarget) : forAnchor;
                case SwitchStatement switching:
                    return Switch(switching, next, continueTarget);
                case Expression expression:
                    return Expression(expression, next);
                case TryStatement:
                    return Unsupported(statement, next, "try/catch/finallyの例外経路は展開しません。");
                default:
                    return Unsupported(statement, next, $"{statement.TypeText}の詳細な制御フローは未対応です。");
            }
        }
        finally { --depth; }
    }

    private string Sequence(IReadOnlyList<Statement> statements, string next, string? breakTarget, string? continueTarget)
    {
        for (var i = statements.Count - 1; i >= 0; --i)
            next = Statement(statements[i], next, breakTarget, continueTarget);
        return next;
    }

    private string Switch(SwitchStatement statement, string next, string? continueTarget)
    {
        // caseの本体はfall-throughを保ち、照合は記述順に行う。defaultは一致なしの場合だけ選ぶ。
        var bodyStarts = new string[statement.Cases.Count];
        var bodyNext = next;
        var defaultTarget = next;
        for (var i = statement.Cases.Count - 1; i >= 0; --i)
        {
            var current = statement.Cases[i];
            bodyStarts[i] = bodyNext = Sequence(current.Consequent.ToArray(), bodyNext, next, continueTarget);
            if (current.Test is null) defaultTarget = bodyStarts[i];
        }
        var compareNext = defaultTarget;
        for (var i = statement.Cases.Count - 1; i >= 0; --i)
        {
            if (statement.Cases[i].Test is not { } test) continue;
            var comparison = Add(NodeKind.Condition, "case " + Text(test) + " に一致", test);
            Connect(comparison, bodyStarts[i], "True");
            Connect(comparison, compareNext, "False");
            compareNext = Expression(test, comparison);
        }
        var input = Add(NodeKind.Transfer, "switch (" + Text(statement.Discriminant) + ")", statement.Discriminant);
        Connect(input, compareNext);
        return Expression(statement.Discriminant, input);
    }

    private string Condition(Expression expression, string yes, string no)
    {
        token.ThrowIfCancellationRequested();
        if (expression is LogicalExpression logical)
        {
            if (logical.Operator == Operator.LogicalAnd)
                return Condition(logical.Left, Condition(logical.Right, yes, no), no);
            if (logical.Operator == Operator.LogicalOr)
                return Condition(logical.Left, yes, Condition(logical.Right, yes, no));
        }
        if (expression is ConditionalExpression ternary)
            return Condition(ternary.Test, Condition(ternary.Consequent, yes, no), Condition(ternary.Alternate, yes, no));
        var condition = Add(NodeKind.Condition, Text(expression), expression);
        Connect(condition, yes, "True");
        Connect(condition, no, "False");
        return Expression(expression, condition);
    }

    private string Expression(Expression expression, string next)
    {
        token.ThrowIfCancellationRequested();
        if (++depth > 128) throw new AnalysisLimitException();
        try
        {
            switch (expression)
            {
                case IFunction:
                case Literal:
                case Identifier:
                case ThisExpression:
                    return next;
                case LogicalExpression logical when logical.Operator == Operator.LogicalAnd:
                    return Condition(logical.Left, Expression(logical.Right, next), next);
                case LogicalExpression logical when logical.Operator == Operator.LogicalOr:
                    return Condition(logical.Left, next, Expression(logical.Right, next));
                case LogicalExpression logical:
                    var nullish = Add(NodeKind.Condition, Text(logical.Left) + " is null / undefined", logical.Left);
                    Connect(nullish, Expression(logical.Right, next), "True");
                    Connect(nullish, next, "False");
                    return Expression(logical.Left, nullish);
                case ConditionalExpression ternary:
                    return Condition(ternary.Test, Expression(ternary.Consequent, next), Expression(ternary.Alternate, next));
                case CallExpression call:
                    var request = owner.Request(call);
                    var target = owner.ResolveCall(call, entry);
                    var label = request is null ? Text(call) : $"HTTP {request.HttpMethod} {request.Url}" +
                        (request.IsAsync ? "（非同期送信）" : "（同期送信）") + (request.IsResolved ? "" : " · 未解決");
                    var callNode = Add(request is null ? NodeKind.Call : NodeKind.Transfer, label, call, target);
                    Connect(callNode, next);
                    if (request is { IsResolved: false }) MarkPartial("XMLHttpRequestの送信内容に未解決の値があります。");
                    if (call.Callee is Identifier { Name: "eval" }) MarkPartial("evalで生成される処理は解析しません。");
                    // コールバックの関数本体は引数評価に含めない。
                    var callFirst = callNode;
                    for (var i = call.Arguments.Count - 1; i >= 0; --i) callFirst = Expression(call.Arguments[i], callFirst);
                    return Expression(call.Callee, callFirst);
                case AssignmentExpression assignment:
                    var assignmentNode = Add(NodeKind.Transfer, Text(assignment), assignment);
                    Connect(assignmentNode, next);
                    var right = Expression(assignment.Right, assignmentNode);
                    if (assignment.Left is Expression left)
                    {
                        if (assignment.Operator == Operator.LogicalAndAssignment) return Condition(left, right, next);
                        if (assignment.Operator == Operator.LogicalOrAssignment) return Condition(left, next, right);
                        if (assignment.Operator == Operator.NullishCoalescingAssignment)
                        {
                            var nullishAssignment = Add(NodeKind.Condition, Text(left) + " is null / undefined", left);
                            Connect(nullishAssignment, right, "True");
                            Connect(nullishAssignment, next, "False");
                            return Expression(left, nullishAssignment);
                        }
                        return Expression(left, right);
                    }
                    MarkPartial("分割代入の書き込み先の評価は詳細表示していません。");
                    return right;
                case AwaitExpression awaiting:
                    var awaitNode = Add(NodeKind.Transfer, "await 完了後に継続", awaiting);
                    Connect(awaitNode, next);
                    MarkPartial("awaitの拒否・キャンセル経路は解決しません。");
                    return Expression(awaiting.Argument, awaitNode);
                case MemberExpression member:
                    var memberNext = member.Computed ? Expression(member.Property, next) : next;
                    return Expression(member.Object, memberNext);
                case UpdateExpression update:
                    var updateNode = Add(NodeKind.Transfer, Text(update), update);
                    Connect(updateNode, next);
                    return Expression(update.Argument, updateNode);
                case NewExpression creation:
                    var createNode = Add(NodeKind.Call, Text(creation), creation);
                    Connect(createNode, next);
                    var createFirst = createNode;
                    for (var i = creation.Arguments.Count - 1; i >= 0; --i) createFirst = Expression(creation.Arguments[i], createFirst);
                    return Expression(creation.Callee, createFirst);
                case ChainExpression:
                    return Unsupported(expression, next, "optional chainingの詳細な評価経路は未対応です。");
                case YieldExpression:
                    return Unsupported(expression, next, "generatorの中断・再開の詳細な経路は未対応です。");
                case TaggedTemplateExpression:
                    return Unsupported(expression, next, "タグ付きテンプレートの詳細な呼び出し評価は未対応です。");
                default:
                    // 算術式等は子式の評価順を保つ。関数本体はIFunctionの分岐で除外する。
                    var children = ChildExpressions(expression).ToArray();
                    for (var i = children.Length - 1; i >= 0; --i) next = Expression(children[i], next);
                    return next;
            }
        }
        finally { --depth; }
    }

    private static IEnumerable<Expression> ChildExpressions(Node node)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is Expression expression) yield return expression;
            else foreach (var nested in ChildExpressions(child)) yield return nested;
        }
    }

    private string Unsupported(Node node, string next, string notice)
    {
        MarkPartial(notice);
        var unknown = Add(NodeKind.Exception, Text(node) + " · 詳細未解決", node);
        Connect(unknown, next, "継続候補");
        return unknown;
    }

    private string Add(NodeKind kind, string label, Node? node, int? target = null, bool ignoreLimit = false,
        SourceRange? source = null)
    {
        token.ThrowIfCancellationRequested();
        if (!ignoreLimit && nodes.Count >= 1500) throw new AnalysisLimitException();
        var id = "js" + nextId++;
        nodes.Add(new FlowNode(id, kind, label, source ?? (node is null ? entry.Entry.Source : owner.Range(entry.Unit, node)),
            TargetMethod: target, TargetFilePath: target is null ? null : owner.TargetPath));
        return id;
    }

    private void Connect(string from, string to, string? label = null) => edges.Add(new FlowEdge(from, to, label));
    private string Text(Node node) => RazorJavaScript.Slice(entry.Unit, node);
    private void MarkPartial(string message)
    {
        partial = true;
        if (!notices.Contains(message)) notices.Add(message);
    }
    private sealed class AnalysisLimitException : Exception;
}
