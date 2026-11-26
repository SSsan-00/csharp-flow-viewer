using Acornima.Ast;

namespace Flow.Analysis;

internal static class JavaScriptWriteTargets
{
    public static bool IsEventTarget(Node target) => target is MemberExpression member
        && RazorJavaScript.PropertyName(member) is { Length: > 2 } name && name.StartsWith("on", StringComparison.Ordinal);

    public static IEnumerable<Node> Leaves(Node pattern)
    {
        var pending = new Stack<Node>();
        pending.Push(pattern);
        while (pending.TryPop(out var node))
            switch (node)
            {
                case Identifier or MemberExpression:
                    yield return node;
                    break;
                case AssignmentPattern initializer:
                    pending.Push(initializer.Left);
                    break;
                case AssignmentProperty property:
                    pending.Push(property.Value);
                    break;
                case RestElement rest:
                    pending.Push(rest.Argument);
                    break;
                case ArrayPattern array:
                    foreach (var item in array.Elements)
                        if (item is not null) pending.Push(item);
                    break;
                case ObjectPattern obj:
                    foreach (var property in obj.Properties) pending.Push(property);
                    break;
                case VariableDeclaration declaration:
                    foreach (var variable in declaration.Declarations) pending.Push(variable.Id);
                    break;
            }
    }
}
