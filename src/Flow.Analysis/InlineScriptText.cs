using System.Net;

namespace Flow.Analysis;

internal sealed record InlineScriptText(string Text, string Encoded, int[]? Offsets)
{
    public int EncodedOffset(int offset) => Offsets?[offset] ?? offset;

    public static InlineScriptText Decode(string encoded, CancellationToken token)
    {
        var decoded = WebUtility.HtmlDecode(encoded);
        if (decoded.Length == encoded.Length) return new(decoded, encoded, null);

        var offsets = new int[decoded.Length + 1];
        int original = 0, projected = 0;
        while (original < encoded.Length)
        {
            token.ThrowIfCancellationRequested();
            if (encoded[original] == '&')
            {
                var tail = encoded.AsSpan(original + 1);
                var terminator = tail.IndexOfAny(';', '&');
                if (terminator >= 0 && tail[terminator] == ';')
                {
                    var entity = encoded.Substring(original, terminator + 2);
                    var value = WebUtility.HtmlDecode(entity);
                    if (value.Length < entity.Length)
                    {
                        for (var i = 0; i < value.Length; i++) offsets[projected++] = original;
                        original += entity.Length;
                        continue;
                    }
                }
            }
            offsets[projected++] = original++;
        }
        offsets[projected] = encoded.Length;
        return new(decoded, encoded, offsets);
    }
}
