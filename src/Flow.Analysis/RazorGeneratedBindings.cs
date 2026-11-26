using Acornima;
using Acornima.Ast;

namespace Flow.Analysis;

internal sealed class RazorGeneratedBindings
{
    private readonly HashSet<string> blocked = new(StringComparer.Ordinal);

    public static RazorGeneratedBindings? Changes(Node root, CancellationToken token)
    {
        var result = new RazorGeneratedBindings();
        foreach (var node in RazorJavaScript.Descendants(root))
        {
            token.ThrowIfCancellationRequested();
            if (node is WithStatement or CallExpression { Callee: Identifier { Name: "eval" } }) return null;
            var write = node switch
            {
                VariableDeclarator declaration => declaration.Id,
                FunctionDeclaration declaration => declaration.Id,
                ClassDeclaration declaration => declaration.Id,
                CatchClause clause => clause.Param,
                AssignmentExpression assignment => assignment.Left,
                UpdateExpression update => update.Argument,
                UnaryExpression { Operator: Operator.Delete } remove => remove.Argument,
                ForInStatement loop => loop.Left,
                ForOfStatement loop => loop.Left,
                _ => null
            };
            if (write is null) continue;
            if (write is MemberExpression member)
            {
                if (member.Object is Identifier { Name: "window" or "globalThis" or "self" })
                {
                    if (RazorJavaScript.PropertyName(member) is not { } name) return null;
                    result.blocked.Add(name);
                }
                else if (member.Object is Identifier receiver) result.blocked.Add(receiver.Name);
            }
            else
                foreach (var identifier in RazorJavaScript.Descendants(write).OfType<Identifier>())
                    result.blocked.Add(identifier.Name);
        }
        return result;
    }

    public bool Allows(Expression callee) => callee switch
    {
        Identifier name => !blocked.Contains(name.Name),
        MemberExpression { Object: Identifier receiver } member when RazorJavaScript.PropertyName(member) is { } name
            => !blocked.Contains(receiver.Name) && (receiver.Name != "window" || !blocked.Contains(name)),
        _ => false
    };
}
