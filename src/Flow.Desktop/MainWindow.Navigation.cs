using System.Windows;
using System.Windows.Input;
using Flow.Model;

namespace Flow.Desktop;

public partial class MainWindow
{
    private bool pendingNodeCenter;

    private void OpenGoToLine()
    {
        if (source is null) return;
        GoToPanel.Visibility = Visibility.Visible;
        GoToLineInput.Text = Editor.TextArea.Caret.Line.ToString();
        GoToLineInput.Focus();
        GoToLineInput.SelectAll();
    }

    private void CloseGoToLineClick(object sender, RoutedEventArgs args) => CloseGoToLine();

    private void CloseGoToLine()
    {
        GoToPanel.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private void GoToLineKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            args.Handled = true;
            GoToLine();
        }
        else if (args.Key == Key.Escape)
        {
            args.Handled = true;
            CloseGoToLine();
        }
    }

    private void GoToLineClick(object sender, RoutedEventArgs args) => GoToLine();

    private void GoToLine()
    {
        if (!int.TryParse(GoToLineInput.Text, out var line) || line < 1 || line > Editor.LineCount)
        {
            SetStatus($"1 から {Editor.LineCount:N0} までの行番号を入力してください。", attention: true);
            GoToLineInput.Focus();
            GoToLineInput.SelectAll();
            return;
        }
        CloseGoToLine();
        NavigateSource(new SourceRange(Editor.Document.GetLineByNumber(line).Offset, 0, line));
        SetStatus($"{line:N0} 行目に移動しました。");
    }

    private void EditorClicked(object sender, MouseButtonEventArgs args)
    {
        if (busy || diagram is null || !Editor.TextArea.TextView.IsMouseOver
            || Editor.GetPositionFromPoint(args.GetPosition(Editor)) is not { } position)
            return;

        var offset = Editor.Document.GetOffset(position.Location);
        var node = NodesForClick(offset, position.Line);
        if (node is null) return;

        SelectNode(node, navigateSource: false);
        if (previewUnavailable || !ready)
        {
            NodesBox.SelectedItem = node;
            NodesBox.ScrollIntoView(node);
            return;
        }

        showList = false;
        UpdateSurface();
        if (rendered)
            preview.Send(new { type = "select", id = node.Id, center = true });
        else
            pendingNodeCenter = true;
    }

    private FlowNode? NodesForClick(int offset, int line) =>
        nodes.Values.Where(node => node.Source is { } range
                && (range.Line == line || (offset >= range.Offset && offset - range.Offset < range.Length)))
            .MinBy(node =>
            {
                var range = node.Source!;
                var distance = Math.Max(0, Math.Max(range.Offset - offset, offset - range.Offset - range.Length + 1));
                return (distance, range.Length == 0 ? int.MaxValue : range.Length);
            });
}
