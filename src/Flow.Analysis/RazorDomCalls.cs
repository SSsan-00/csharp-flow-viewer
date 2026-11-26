using Acornima.Ast;
using Scope = Flow.Analysis.RazorJavaScript.Scope;

namespace Flow.Analysis;

internal static class RazorDomCalls
{
    public static bool IsRoutine(CallExpression call, Scope scope, RazorGeneratedBindings? generated = null)
    {
        if (scope.HasDynamicBinding || call.Callee is not MemberExpression member
            || RazorJavaScript.PropertyName(member) is not { } name) return false;

        var receiver = member.Object;
        if (IsDocument(receiver, scope, generated))
            return name is "getElementById" or "querySelector" or "querySelectorAll"
                or "getElementsByClassName" or "getElementsByTagName" or "getElementsByTagNameNS" or "getElementsByName";
        if (receiver is MemberExpression classes && RazorJavaScript.PropertyName(classes) == "classList")
            return (name is "add" or "remove" or "toggle" or "replace" or "contains")
                && IsElement(classes.Object, scope, generated);
        return (name is "querySelector" or "querySelectorAll" or "closest" or "matches"
            or "getElementsByClassName" or "getElementsByTagName" or "getElementsByTagNameNS"
            or "setAttribute" or "removeAttribute" or "toggleAttribute" or "getAttribute" or "hasAttribute")
            && IsElement(receiver, scope, generated);
    }

    public static bool IsElement(Expression expression, Scope scope, RazorGeneratedBindings? generated = null, int depth = 0)
    {
        if (depth >= 16) return false;
        if (expression is Identifier id)
            return generated?.IsUnchanged(id.Name) != false && scope.IsDomElement(id.Name);
        if (expression is not CallExpression { Callee: MemberExpression member }) return false;
        var name = RazorJavaScript.PropertyName(member);
        return ((name is "getElementById" or "querySelector") && IsDocument(member.Object, scope, generated))
            || ((name is "querySelector" or "closest") && IsElement(member.Object, scope, generated, depth + 1));
    }

    private static bool IsDocument(Expression expression, Scope scope, RazorGeneratedBindings? generated) =>
        generated?.IsUnchanged("document") != false
        && !scope.IsDeclared("document")
        && (expression is not MemberExpression || generated?.IsUnchanged("window") != false)
        && RazorJavaScript.IsDocument(expression, scope);
}
