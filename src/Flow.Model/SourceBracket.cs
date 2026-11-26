namespace Flow.Model;

public readonly record struct SourceBracket(int Open, int Close, bool CanFold = false)
{
    public static int? MatchAt(IEnumerable<SourceBracket> pairs, int caret)
    {
        int? previous = null;
        foreach (var pair in pairs)
        {
            if (pair.Open == caret) return pair.Close;
            if (pair.Close == caret) return pair.Open;
            if (pair.Open == caret - 1) previous = pair.Close;
            if (pair.Close == caret - 1) previous = pair.Open;
        }
        return previous;
    }
}
