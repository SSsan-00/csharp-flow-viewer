using Flow.Model;

namespace Flow.Desktop;

internal static class SourceNodePicker
{
    public static FlowNode? Find(IEnumerable<FlowNode> nodes, string? file, int offset, int line, bool inlineTerminator = false)
    {
        FlowNode? best = null;
        var score = (Distance: int.MaxValue, Length: int.MaxValue);
        foreach (var node in nodes)
        {
            if (node.Source is not { } range || range.FilePath is not null
                && !string.Equals(range.FilePath, file, StringComparison.OrdinalIgnoreCase)) continue;
            var distance = Math.Max(0, Math.Max(range.Offset - offset, offset - range.Offset - range.Length + 1));
            if (distance != 0 && range.Line != line) continue;
            // In an inline handler, a semicolon immediately after a call belongs to that statement.
            var rank = inlineTerminator && node.Kind == NodeKind.Call && distance == 1
                && range.Length > 0 && offset == range.Offset + range.Length ? -1 : distance;
            var candidate = (rank, range.Length == 0 ? int.MaxValue : range.Length);
            if (best is not null && candidate.CompareTo(score) >= 0) continue;
            best = node;
            score = candidate;
        }
        return best;
    }
}
