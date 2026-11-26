using Flow.Model;

namespace Flow.Desktop;

internal readonly record struct SourceAnnotationRange(int Offset, int Length, bool Marked)
{
    public static SourceAnnotationRange? ForNode(FlowNode node, bool explicitMark, bool visibleMark, bool gray,
        int textLength, Func<int, int> lineEnd)
    {
        if (node.Source is not { } source || source.Offset < 0 || source.Offset >= textLength || source.Length < 0)
            return null;

        var automatic = !explicitMark && !gray && visibleMark && node.Kind is not (NodeKind.Start or NodeKind.End);
        if (!explicitMark && !gray && !automatic) return null;

        // An automatically marked control node can contain the rest of a function or an entire branch.
        var length = Math.Min(source.Length, textLength - source.Offset);
        if (automatic || length == 0)
        {
            var availableOnLine = lineEnd(source.Offset) - source.Offset;
            length = length == 0 ? availableOnLine : Math.Min(length, availableOnLine);
        }
        return length > 0 ? new SourceAnnotationRange(source.Offset, length, explicitMark || automatic) : null;
    }
}
