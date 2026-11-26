using Acornima.Ast;
using Scope = Flow.Analysis.RazorJavaScript.Scope;

namespace Flow.Analysis;

internal static class RazorBrowserCalls
{
    public static bool IsRoutine(CallExpression call, Scope scope, RazorGeneratedBindings? generated = null,
        bool isStandaloneFocus = false)
    {
        if (scope.HasDynamicBinding) return false;
        if (call.Callee is Identifier { Name: "$" or "jQuery" } && call.Arguments.Count > 0
            && call.Arguments[0] is StringLiteral literal && !literal.Value.TrimStart().StartsWith('<'))
            return RazorJavaScript.IsJQuery(call, scope, generated);
        if (call.Callee is not MemberExpression member || RazorJavaScript.PropertyName(member) is not { } name) return false;

        var receiver = member.Object;
        if (IsXhrMethod(name) || name == "addEventListener" && call.Arguments.Count > 0
            && call.Arguments[0] is StringLiteral { Value: "readystatechange" })
            return IsXhr(receiver, scope, generated);
        if (IsJQueryRoutineMember(name)) return RazorJavaScript.IsJQuery(receiver, scope, generated);
        if (IsDocument(receiver, scope, generated))
            return IsDocumentLookup(name);
        if (receiver is MemberExpression classes && RazorJavaScript.PropertyName(classes) == "classList")
            return (name is "add" or "remove" or "toggle" or "replace" or "contains")
                && IsElement(classes.Object, scope, generated);
        if (isStandaloneFocus && receiver is Identifier obj && !scope.HasDomOverride(obj.Name)
            && (generated?.InitializerAt(obj) is { } initializer
                ? !IsExplicitFocusReceiver(initializer, scope)
                : generated?.IsReceiverUnchanged(obj.Name) != false && !scope.IsExplicitFocusReceiver(obj.Name)))
            return true;
        return IsElementMember(name) && IsElement(receiver, scope, generated);
    }

    public static CallExpression? StandaloneFocus(Node node) => node is ExpressionStatement
        { Expression: CallExpression { Callee: MemberExpression { Object: Identifier { Name: "obj" } } member } call }
        && RazorJavaScript.PropertyName(member) == "focus" ? call : null;

    public static bool IsExplicitFocusReceiver(Expression expression, Scope scope) =>
        expression is ObjectExpression or NewExpression or IFunction or ClassExpression
        || expression is Identifier name
            && (scope.IsExplicitFocusReceiver(name.Name) || scope.Resolve(name.Name) is not null);

    public static bool IsElement(Expression expression, Scope scope, RazorGeneratedBindings? generated = null, int depth = 0)
    {
        if (depth >= 16) return false;
        if (expression is Identifier id)
        {
            if (generated?.InitializerAt(id) is { } initializer)
                return IsElement(initializer, scope, generated, depth + 1);
            return generated?.IsReceiverUnchanged(id.Name) != false && scope.IsDomElement(id.Name);
        }
        if (expression is MemberExpression { Computed: true } indexed)
        {
            if (indexed.Object is MemberExpression collection)
                return RazorJavaScript.PropertyName(collection) is "forms" or "images" or "links"
                    && IsDocument(collection.Object, scope, generated);
            if (indexed.Object is CallExpression { Callee: MemberExpression lookup })
                return RazorJavaScript.PropertyName(lookup) is { } method && IsDocumentLookup(method)
                    && method is not ("getElementById" or "querySelector") && IsDocument(lookup.Object, scope, generated);
        }
        if (expression is not CallExpression { Callee: MemberExpression member }) return false;
        var name = RazorJavaScript.PropertyName(member);
        return ((name is "getElementById" or "querySelector") && IsDocument(member.Object, scope, generated))
            || ((name is "querySelector" or "closest") && IsElement(member.Object, scope, generated, depth + 1));
    }

    public static bool CouldOverrideRoutineMethod(MemberExpression member) => RazorJavaScript.PropertyName(member) is not { } name
        || IsDocumentLookup(name) || IsElementMember(name) || IsJQueryRoutineMember(name) || IsXhrMethod(name) || name == "classList";

    public static bool IsReadyStateHandler(MemberExpression member, Scope scope, RazorGeneratedBindings? generated = null) =>
        !scope.HasDynamicBinding && RazorJavaScript.PropertyName(member) == "onreadystatechange"
        && IsXhr(member.Object, scope, generated);

    private static bool IsXhr(Expression expression, Scope scope, RazorGeneratedBindings? generated, int depth = 0)
    {
        if (depth >= 16) return false;
        if (expression is Identifier id)
        {
            if (generated?.InitializerAt(id) is { } initializer) return IsXhr(initializer, scope, generated, depth + 1);
            return generated?.IsReceiverUnchanged(id.Name) != false && scope.FindXhr(id.Name) is not null && !scope.HasDomOverride(id.Name);
        }
        return expression is NewExpression { Callee: Identifier constructor }
            && generated?.IsUnchanged(constructor.Name) != false && RazorJavaScript.IsXhrConstructor(expression, scope, out _);
    }

    public static bool IsJQueryRoutineMember(string name) => name is "off" or "removeAttr" or "attr";

    private static bool IsXhrMethod(string name) => name is "open" or "send" or "setRequestHeader" or "abort"
        or "getAllResponseHeaders" or "getResponseHeader" or "overrideMimeType";

    private static bool IsDocumentLookup(string name) => name is "getElementById" or "querySelector" or "querySelectorAll"
        or "getElementsByClassName" or "getElementsByTagName" or "getElementsByTagNameNS" or "getElementsByName";

    private static bool IsElementMember(string name) => name is "querySelector" or "querySelectorAll" or "closest" or "matches"
        or "getElementsByClassName" or "getElementsByTagName" or "getElementsByTagNameNS"
        or "setAttribute" or "removeAttribute" or "toggleAttribute" or "getAttribute" or "hasAttribute"
        or "focus" or "blur" or "submit" or "requestSubmit";

    private static bool IsDocument(Expression expression, Scope scope, RazorGeneratedBindings? generated) =>
        generated?.IsUnchanged("document") != false
        && !scope.IsDeclared("document")
        && !scope.HasDomOverride("document")
        && (expression is not MemberExpression || generated?.IsUnchanged("window") != false)
        && RazorJavaScript.IsDocument(expression, scope);
}
