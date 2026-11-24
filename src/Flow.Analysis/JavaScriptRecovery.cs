using Acornima;
using Acornima.Ast;

namespace Flow.Analysis;

internal sealed record JavaScriptRecovery(Script Program, int[] Insertions)
{
    private const int MaxRepairs = 4;
    private const int MaxCharacters = 2 * 1024 * 1024;

    public int ParsedLengthDelta => Insertions.Length;

    public int SourceOffset(int parsedOffset)
    {
        var index = Array.BinarySearch(Insertions, parsedOffset);
        return parsedOffset - (index < 0 ? ~index : index);
    }

    public static JavaScriptRecovery? TryParse(string source, ParseError firstError, CancellationToken token)
    {
        if (source.Length > MaxCharacters || !IsAtRecoverableSemicolon(source, firstError)) return null;
        var insertions = new List<int>(2 * MaxRepairs);
        var repairs = 0;
        var projected = source;
        Token previous = default;
        var parser = new Parser(new ParserOptions
        {
            OnToken = (in Token current) =>
            {
                token.ThrowIfCancellationRequested();
                previous = current;
            }
        });

        while (true)
        {
            token.ThrowIfCancellationRequested();
            previous = default;
            try
            {
                var program = parser.ParseScript(projected);
                return insertions.Count == 0 ? null : new(program, insertions.ToArray());
            }
            catch (ParseErrorException exception)
            {
                var error = exception.Error;
                if (repairs == MaxRepairs || !IsAtRecoverableSemicolon(projected, error) || previous.End > error.Index
                    || insertions.Count > 0 && error.Index <= insertions[^1])
                    return null;
                var filler = error.Code switch
                {
                    "UnexpectedToken" when previous.Kind == TokenKind.Punctuator && previous.Value is "=" => "0",
                    "DeclarationMissingInitializer_Const" when previous.Kind == TokenKind.Identifier => "=0",
                    _ => null
                };
                if (filler is null) return null;

                // Keep the LHS and statement boundary intact. The filler is never shown as source.
                for (var i = 0; i < filler.Length; i++) insertions.Add(error.Index + i);
                projected = projected.Insert(error.Index, filler);
                repairs++;
            }
        }
    }

    private static bool IsAtRecoverableSemicolon(string text, ParseError error) =>
        error.Code is "UnexpectedToken" or "DeclarationMissingInitializer_Const"
        && error.IsIndexDefined && (uint)error.Index < (uint)text.Length && text[error.Index] == ';';
}
