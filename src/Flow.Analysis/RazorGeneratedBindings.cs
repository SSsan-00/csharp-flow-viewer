using Acornima;
using Acornima.Ast;

namespace Flow.Analysis;

internal sealed class RazorGeneratedBindings
{
    private readonly HashSet<string> blocked = new(StringComparer.Ordinal);
    private readonly HashSet<string> changedReceivers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VariableDeclarator> staticInitializers = new(StringComparer.Ordinal);
    private readonly HashSet<CallExpression> standaloneFocus = [];

    public static RazorGeneratedBindings? Changes(Node root, CancellationToken token)
    {
        var result = new RazorGeneratedBindings();
        if (root is Acornima.Ast.Program program)
            foreach (var declaration in program.Body.OfType<VariableDeclaration>())
                foreach (var variable in declaration.Declarations)
                    if (variable.Id is Identifier name && variable.Init is not null)
                        result.staticInitializers.TryAdd(name.Name, variable);
        foreach (var node in RazorJavaScript.Descendants(root))
        {
            token.ThrowIfCancellationRequested();
            if (node is WithStatement or CallExpression { Callee: Identifier { Name: "eval" } }) return null;
            if (RazorBrowserCalls.StandaloneFocus(node) is { } call) result.standaloneFocus.Add(call);
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
            foreach (var target in JavaScriptWriteTargets.Leaves(write))
            {
                if (target is Identifier identifier)
                {
                    result.blocked.Add(identifier.Name);
                    result.changedReceivers.Add(identifier.Name);
                    if (result.staticInitializers.TryGetValue(identifier.Name, out var declaration) && !ReferenceEquals(node, declaration))
                        result.staticInitializers.Remove(identifier.Name);
                }
                else if (target is MemberExpression member)
                {
                    if (member.Object is Identifier { Name: "window" or "globalThis" or "self" })
                    {
                        if (RazorJavaScript.PropertyName(member) is not { } name) return null;
                        result.blocked.Add(name);
                        result.changedReceivers.Add(name);
                        result.staticInitializers.Remove(name);
                    }
                    else if (member.Object is Identifier receiver)
                    {
                        result.blocked.Add(receiver.Name);
                        if (RazorBrowserCalls.CouldOverrideRoutineMethod(member))
                        {
                            result.changedReceivers.Add(receiver.Name);
                            result.staticInitializers.Remove(receiver.Name);
                        }
                    }
                }
            }
        }
        return result;
    }

    public bool IsUnchanged(string name) => !blocked.Contains(name);

    public bool IsStandaloneFocus(CallExpression call) => standaloneFocus.Contains(call);

    public bool IsReceiverUnchanged(string name) => !changedReceivers.Contains(name);

    public Expression? InitializerAt(Identifier name) => staticInitializers.TryGetValue(name.Name, out var declaration)
        && declaration.End <= name.Start ? declaration.Init : null;

    public bool Allows(Expression callee) => callee switch
    {
        Identifier name => !blocked.Contains(name.Name),
        MemberExpression { Object: Identifier receiver } member when RazorJavaScript.PropertyName(member) is { } name
            => !blocked.Contains(receiver.Name) && (receiver.Name != "window" || !blocked.Contains(name)),
        _ => false
    };
}
