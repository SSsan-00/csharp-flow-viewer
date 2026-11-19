using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Flow.Analysis;
using Flow.Model;

namespace Flow.Desktop;

public partial class MainWindow
{
    private bool pendingNodeCenter;
    private Point? editorPress;
    private bool selectingFromSource;

    private void OpenGoToLine()
    {
        if (!HasSource) return;
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

    private void EditorPressed(object sender, MouseButtonEventArgs args)
    {
        editorPress = args.GetPosition(Editor.TextArea.TextView);
    }

    private async void EditorClicked(object sender, MouseButtonEventArgs args)
    {
        var start = editorPress;
        editorPress = null;
        var view = Editor.TextArea.TextView;
        var point = args.GetPosition(view);
        if (busy || !HasSource || start is not { } pressed
            || Math.Abs(pressed.X - point.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(pressed.Y - point.Y) > SystemParameters.MinimumVerticalDragDistance
            || !new Rect(view.RenderSize).Contains(point)
            || Editor.GetPositionFromPoint(args.GetPosition(Editor)) is not { } position)
            return;

        var offset = Editor.Document.GetOffset(position.Location);
        var target = CurrentEntries
            .Where(entry => IsCurrentSource(entry.Source)
                && offset >= entry.Source.Offset && offset < entry.Source.Offset + entry.Source.Length)
            .MinBy(entry => entry.Source.Length);
        if (target is null) return;
        if (method?.Id != target.Id)
        {
            await SelectMethodAsync(target.Id);
            if (closed) return;
            Editor.Select(offset, 0);
            Editor.ScrollTo(position.Line, position.Column);
        }
        if (NodesForClick(offset, position.Line) is { } node)
            JumpToPreviewNode(node, navigateSource: false);
    }

    private void JumpToPreviewNode(FlowNode node, bool navigateSource)
    {
        SelectNode(node, navigateSource);
        if (previewUnavailable || !ready)
        {
            selectingFromSource = true;
            try
            {
                NodesBox.SelectedItem = node;
                NodesBox.ScrollIntoView(node);
            }
            finally
            {
                selectingFromSource = false;
            }
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
        nodes.Values.Where(node => node.Source is { } range && IsCurrentSource(range)
                && (range.Line == line || (offset >= range.Offset && offset - range.Offset < range.Length)))
            .MinBy(node =>
            {
                var range = node.Source!;
                var distance = Math.Max(0, Math.Max(range.Offset - offset, offset - range.Offset - range.Length + 1));
                return (distance, range.Length == 0 ? int.MaxValue : range.Length);
            });

    private bool IsCurrentSource(SourceRange range) => range.FilePath is null
        || string.Equals(range.FilePath, currentFile?.Path, StringComparison.OrdinalIgnoreCase);

    private ProjectFile? FindTargetFile(FlowNode node) => node.TargetFilePath is null
        ? currentFile
        : project?.Files.FirstOrDefault(file =>
            string.Equals(file.Path, node.TargetFilePath, StringComparison.OrdinalIgnoreCase));

    private bool CanFollow(FlowNode node) => node.TargetMethod is not null && FindTargetFile(node) is not null;

    // Navigation keeps the originating Razor event in the same history as C# calls.
    private async Task NavigateEntryAsync(ProjectFile file, int entryId, string? returnTo = null)
    {
        SetBusy(true);
        try
        {
            if (currentFile != file)
            {
                if (!FilesBox.Items.Contains(file)) FileFilter.Clear();
                FilesBox.SelectedItem = file;
                FilesBox.ScrollIntoView(file);
                await LoadFile(file, preserveHistory: true, restoreSelection: false);
                if (closed) return;
            }
            await SelectMethodAsync(entryId, returnTo);
            if (!closed && HasSource) SaveProjectState();
        }
        catch (Exception exception) when (!closed) { ShowFailure(exception, "呼び出し先に移動できません"); }
        catch (Exception) when (closed) { }
        finally { if (!closed) SetBusy(false); }
    }

    private void UpdateSourcePresentation()
    {
        var isRazor = currentFile?.IsRazor == true;
        SourceLanguage.Text = isRazor ? "Razor" : "C#";
        EntrySearchHint.Text = isRazor ? "画面イベントを検索" : "メソッドを検索";
        Filter.ToolTip = isRazor ? "イベント名、要素名、または行番号" : "名前、シグネチャ、または行番号";
        AutomationProperties.SetName(Filter, EntrySearchHint.Text);
        AutomationProperties.SetName(MethodsBox, isRazor ? "画面イベント" : "メソッド");
        AutomationProperties.SetName(Editor, isRazor ? "Razor ソース" : "C# ソース");
        ApplyTheme();
    }
}
