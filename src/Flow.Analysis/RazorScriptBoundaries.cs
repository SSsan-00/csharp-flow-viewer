using Acornima;
using Acornima.Ast;
using Flow.Model;
using JsProgram = Acornima.Ast.Program;

namespace Flow.Analysis;

internal static class RazorScriptBoundaries
{
    public static HashSet<int> IndependentSlots(JsProgram root) =>
        RazorJavaScript.Descendants(root).OfType<EmptyStatement>().Select(node => node.Start).ToHashSet();

    public static (string Text, IReadOnlyDictionary<int, SourceRange> Slots)? RestoreStatementAsi(
        string projected, JsProgram root, IReadOnlyDictionary<int, SourceRange> slots, IReadOnlySet<int> independent,
        CancellationToken token)
    {
        var ends = new Dictionary<int, int>();
        // A direct statement-list member can end here without changing the association of an if, loop or label.
        foreach (var node in RazorJavaScript.Descendants(root))
        {
            token.ThrowIfCancellationRequested();
            if (node is JsProgram program)
                foreach (var statement in program.Body) RememberEnd(statement);
            else if (node is BlockStatement block)
                foreach (var statement in block.Body) RememberEnd(statement);
            else if (node is SwitchCase branch)
                foreach (var statement in branch.Consequent) RememberEnd(statement);
        }
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
            if (!ends.TryGetValue(offset, out var end) || end > offset || source.Length < 2 || offset + 1 >= text.Length
                || text[offset] != ';' || text[offset + 1] != ' ' || slots.ContainsKey(offset + 1))
                return null;
            var gap = projected.AsSpan(end, offset - end);
            if (gap.IndexOfAny('\r', '\n') < 0 || !gap.IsWhiteSpace()) return null;

            // The first semicolon closes the JavaScript statement; the second remains a Razor boundary.
            text[offset + 1] = ';';
            newSlots.Add(offset + 1, source);
        }
        return (new string(text), newSlots);

        void RememberEnd(Node statement)
        {
            var terminator = statement.End - 1;
            if (!slots.ContainsKey(terminator)) return;
            var end = statement switch
            {
                ExpressionStatement expression => expression.Expression.End,
                VariableDeclaration { Declarations.Count: > 0 } declaration when declaration.Declarations[^1].Init is not null
                    => declaration.Declarations[^1].End,
                _ => -1
            };
            if (end >= 0) ends.TryAdd(terminator, end);
        }
    }

    public static (JsProgram Root, IReadOnlySet<int> Offsets)? TryParseGeneratedValues(
        Parser parser, string text, IReadOnlyDictionary<int, SourceRange> slots, ParseError error, CancellationToken token)
    {
        const int SequentialRepairs = 4;
        if (text.Length > 2 * 1024 * 1024) return null;
        var projection = text.ToCharArray();
        var offsets = new HashSet<int>();
        while (offsets.Count < SequentialRepairs && CanReplace(error))
        {
            token.ThrowIfCancellationRequested();
            Replace(error.Index);
            try { return ParseProjection(); }
            catch (ParseErrorException retry) { error = retry.Error; }
        }

        if (offsets.Count != SequentialRepairs || !CanReplace(error)) return null;
        // Shortlist the remaining value markers for one more parse; their AST roles, not the punctuation, decide validity.
        Replace(error.Index);
        foreach (var position in slots.Keys)
        {
            token.ThrowIfCancellationRequested();
            if (projection[position] != ';') continue;
            var previous = position - 1;
            while (previous >= 0 && char.IsWhiteSpace(projection[previous])) previous--;
            if (previous >= 0 && projection[previous] is '=' or '[' or ',') Replace(position);
        }
        try { return ParseProjection(); }
        catch (ParseErrorException) { return null; }

        bool CanReplace(ParseError failure) => failure.Code == "UnexpectedToken" && failure.IsIndexDefined
            && slots.ContainsKey(failure.Index) && projection[failure.Index] == ';';

        void Replace(int position)
        {
            projection[position] = '0';
            offsets.Add(position);
        }

        (JsProgram Root, IReadOnlySet<int> Offsets)? ParseProjection()
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
    }

    public static (JsProgram Root, int End)? ReadExplicitPrefix(string projected, bool allowReturn, CancellationToken token)
    {
        var depth = 0;
        var lastTerminator = 0;
        var parser = new Parser(new ParserOptions
        {
            AllowReturnOutsideFunction = allowReturn,
            OnToken = (in Token current) =>
            {
                token.ThrowIfCancellationRequested();
                if (current.Kind != TokenKind.Punctuator) return;
                switch (current.Value)
                {
                    case "(" or "[" or "{" or "${": depth++; break;
                    case ")" or "]" or "}": depth--; break;
                    case ";" when depth == 0: lastTerminator = current.End; break;
                }
            }
        });
        try { parser.ParseScript(projected); }
        catch (ParseErrorException) { }
        if (lastTerminator == 0) return null;
        var plainParser = new Parser(new ParserOptions { AllowReturnOutsideFunction = allowReturn });
        try
        {
            var prefix = plainParser.ParseScript(projected[..lastTerminator]);
            for (var i = prefix.Body.Count - 1; i >= 0; i--)
            {
                // A dangling else or an unbraced control body is not an independent boundary.
                var statement = prefix.Body[i];
                if (statement is not (ExpressionStatement or VariableDeclaration or EmptyStatement or ReturnStatement or ThrowStatement)
                    || projected[statement.End - 1] != ';') continue;
                var end = statement.End;
                return (end == lastTerminator ? prefix : plainParser.ParseScript(projected[..end]), end);
            }
        }
        catch (ParseErrorException) { }
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
            var affected = false;
            for (var index = nextSlot; index < positions.Length && positions[index] < declaration.End; index++)
            {
                var position = positions[index];
                if (position > declaration.Body.Start && generatedValues?.Contains(position) == true) continue;
                affected = true;
                break;
            }
            line += fragment.Text.AsSpan(previous, declaration.Start - previous).Count('\n');
            previous = declaration.Start;
            yield return (id.Name, affected || dynamicBindings || blocked.Contains(id.Name) ? null
                : new SourceRange(fragment.Source.Offset + declaration.Start, declaration.End - declaration.Start,
                    line, fragment.Source.FilePath));
        }
    }
}
