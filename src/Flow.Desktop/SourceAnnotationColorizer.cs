using System.Windows.Media;
using Flow.Model;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Flow.Desktop;

internal sealed class SourceAnnotationColorizer : DocumentColorizingTransformer
{
    private sealed class AnnotationSpan : TextSegment
    {
        public bool Marked { get; init; }
    }

    private readonly TextSegmentCollection<AnnotationSpan> ranges = new();
    private static readonly Brush DarkGray = Solid(0x8b, 0x94, 0xa6);
    private static readonly Brush LightGray = Solid(0x66, 0x70, 0x83);
    private static readonly Brush DarkMark = Solid(0x60, 0x47, 0x1a);
    private static readonly Brush LightMark = Solid(0xff, 0xe5, 0xa1);

    public bool Dark { get; set; }

    public void Clear() => ranges.Clear();

    public void SetRanges(TextDocument document, IEnumerable<FlowNode> nodes,
        IReadOnlySet<string> marked, IReadOnlySet<string> gray)
    {
        ranges.Clear();
        foreach (var node in nodes)
        {
            var isMarked = marked.Contains(node.Id);
            if ((!isMarked && !gray.Contains(node.Id)) || node.Source is not { } range ||
                range.Offset >= document.TextLength)
                continue;

            var length = range.Length;
            if (length == 0)
                length = document.GetLineByOffset(range.Offset).EndOffset - range.Offset;
            if (length > 0)
                ranges.Add(new AnnotationSpan { StartOffset = range.Offset, Length = length, Marked = isMarked });
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var span in ranges.FindOverlappingSegments(line.Offset, line.Length))
        {
            var start = Math.Max(span.StartOffset, line.Offset);
            var end = Math.Min(span.EndOffset, line.EndOffset);
            if (start >= end) continue;
            ChangeLinePart(start, end, element =>
            {
                if (span.Marked)
                    element.TextRunProperties.SetBackgroundBrush(Dark ? DarkMark : LightMark);
                else
                    element.TextRunProperties.SetForegroundBrush(Dark ? DarkGray : LightGray);
            });
        }
    }

    private static Brush Solid(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
