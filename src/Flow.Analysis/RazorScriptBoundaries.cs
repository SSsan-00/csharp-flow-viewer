using Acornima.Ast;
using Flow.Model;
using JsProgram = Acornima.Ast.Program;

namespace Flow.Analysis;

internal static class RazorScriptBoundaries
{
    public static HashSet<int> IndependentSlots(JsProgram root) =>
        RazorJavaScript.Descendants(root).OfType<EmptyStatement>().Select(node => node.Start).ToHashSet();

    public static (string Text, IReadOnlyDictionary<int, SourceRange> Slots)? RestoreTopLevelAsi(
        string projected, JsProgram root, IReadOnlyDictionary<int, SourceRange> slots, IReadOnlySet<int> independent,
        CancellationToken token)
    {
        var declarations = root.Body.OfType<VariableDeclaration>().ToDictionary(declaration => declaration.End - 1);
        var newSlots = new Dictionary<int, SourceRange>(slots.Count);
        var text = projected.ToCharArray();
        foreach (var (offset, source) in slots)
        {
            token.ThrowIfCancellationRequested();
            if (independent.Contains(offset))
            {
                newSlots.Add(offset, source);
                continue;
            }
            if (!declarations.TryGetValue(offset, out var declaration) || declaration.Declarations.Count == 0
                || declaration.Declarations[^1].Init is null || source.Length < 2 || offset + 1 >= text.Length
                || text[offset] != ';' || text[offset + 1] != ' ' || slots.ContainsKey(offset + 1))
                return null;
            var end = declaration.Declarations[^1].End;
            if (end > offset) return null;
            var gap = projected.AsSpan(end, offset - end);
            if (gap.IndexOfAny('\r', '\n') < 0 || !gap.IsWhiteSpace()) return null;

            // The first semicolon closes the variable statement; the second remains a Razor boundary.
            text[offset + 1] = ';';
            newSlots.Add(offset + 1, source);
        }
        return (new string(text), newSlots);
    }

    public static IEnumerable<(string Name, SourceRange? Source)> SourcesOutsideUnresolvedReturns(
        RazorJavaScriptFragment fragment, JsProgram root, IReadOnlyDictionary<int, SourceRange> slots,
        IReadOnlySet<int> independent, CancellationToken token)
    {
        var declarations = root.Body.OfType<FunctionDeclaration>().ToArray();
        if (declarations.Length == 0) yield break;
        var returnEnds = new HashSet<int>();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var dynamicBindings = false;
        foreach (var node in RazorJavaScript.Descendants(root))
        {
            token.ThrowIfCancellationRequested();
            if (node is ReturnStatement { Argument: null } returned) returnEnds.Add(returned.End - 1);
            if (node is WithStatement or CallExpression { Callee: Identifier { Name: "eval" } }) dynamicBindings = true;
            var write = node switch
            {
                VariableDeclarator declaration => declaration.Id,
                AssignmentExpression assignment => assignment.Left,
                UpdateExpression update => update.Argument,
                ForInStatement loop => loop.Left,
                ForOfStatement loop => loop.Left,
                ClassDeclaration declaration => declaration.Id,
                _ => null
            };
            if (write is MemberExpression member)
            {
                if (RazorJavaScript.PropertyName(member) is { } name) blocked.Add(name);
                else dynamicBindings = true;
            }
            else if (write is not null)
                foreach (var id in RazorJavaScript.Descendants(write).OfType<Identifier>()) blocked.Add(id.Name);
        }

        var positions = slots.Keys.Order().ToArray();
        var nextDeclaration = 0;
        foreach (var position in positions.Where(position => !independent.Contains(position)))
        {
            while (nextDeclaration < declarations.Length && declarations[nextDeclaration].End <= position)
                nextDeclaration++;
            if (!returnEnds.Contains(position) || nextDeclaration == declarations.Length
                || declarations[nextDeclaration].Start > position) yield break;
        }

        int previous = 0, line = fragment.Source.Line, nextSlot = 0;
        foreach (var declaration in declarations)
        {
            token.ThrowIfCancellationRequested();
            if (declaration.Id is not { } id) continue;
            while (nextSlot < positions.Length && positions[nextSlot] < declaration.Start) nextSlot++;
            var affected = nextSlot < positions.Length && positions[nextSlot] < declaration.End;
            line += fragment.Text.AsSpan(previous, declaration.Start - previous).Count('\n');
            previous = declaration.Start;
            yield return (id.Name, affected || dynamicBindings || blocked.Contains(id.Name) ? null
                : new SourceRange(fragment.Source.Offset + declaration.Start, declaration.End - declaration.Start,
                    line, fragment.Source.FilePath));
        }
    }
}
