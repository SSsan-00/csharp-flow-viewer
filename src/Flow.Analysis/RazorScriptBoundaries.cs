using Acornima;
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

    public static (JsProgram Root, IReadOnlySet<int> Offsets)? TryParseGeneratedValues(
        Parser parser, string text, IReadOnlyDictionary<int, SourceRange> slots, ParseError error, CancellationToken token)
    {
        if (text.Length > 2 * 1024 * 1024) return null;
        var projection = text.ToCharArray();
        var offsets = new HashSet<int>();
        // Replace only a rejected Razor marker, retaining original source offsets.
        while (offsets.Count < 4 && error.Code == "UnexpectedToken" && error.IsIndexDefined
            && slots.ContainsKey(error.Index) && projection[error.Index] == ';' && offsets.Add(error.Index))
        {
            token.ThrowIfCancellationRequested();
            projection[error.Index] = '0';
            try
            {
                var root = parser.ParseScript(new string(projection));
                var pending = new HashSet<int>(offsets);
                foreach (var node in RazorJavaScript.Descendants(root))
                {
                    token.ThrowIfCancellationRequested();
                    if (node is VariableDeclarator { Init: { } value }) CheckValue(value);
                    else if (node is ArrayExpression array)
                        foreach (var element in array.Elements) CheckValue(element);
                }
                return pending.Count == 0 ? (root, offsets) : null;

                void CheckValue(Node? value)
                {
                    if (value is not null && value.End == value.Start + 1) pending.Remove(value.Start);
                }
            }
            catch (ParseErrorException retry) { error = retry.Error; }
        }
        return null;
    }

    public static IEnumerable<(string Name, SourceRange? Source)> SourcesOutsideUnresolvedSlots(
        RazorJavaScriptFragment fragment, JsProgram root, IReadOnlyDictionary<int, SourceRange> slots,
        IReadOnlySet<int> independent, CancellationToken token, IReadOnlySet<int>? generatedValues = null)
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
            if (generatedValues?.Contains(position) == true) continue;
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
