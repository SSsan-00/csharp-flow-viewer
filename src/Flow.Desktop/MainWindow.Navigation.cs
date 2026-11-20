using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Flow.Analysis;
using Flow.Model;

namespace Flow.Desktop;

public partial class MainWindow
{
    private bool pendingNodeCenter;
    private Point? editorPress;
    private bool selectingFromSource;
    private DefinitionView definitionView;
    private CallerSearch? callerSearch;
    private ContextMenu? navigationMenu;

    private enum DefinitionView { Direct, ImplementationCandidate }

    private void ShowDefinitionView(DefinitionView view)
    {
        definitionView = view;
        DefinitionBanner.Text = view switch
        {
            DefinitionView.ImplementationCandidate => "実装候補のフロー · 実際に呼ばれる実装とは限りません。",
            _ => ""
        };
        DefinitionBanner.Visibility = view == DefinitionView.Direct ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearDeclarationCandidates()
    {
        CandidateBox.ItemsSource = null;
        CandidatePanel.Visibility = Visibility.Collapsed;
    }

    private void CandidateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs args) =>
        OpenCandidateButton.IsEnabled = !busy && CandidateBox.SelectedItem is ImplementationCandidate;

    private async void OpenCandidate(object sender, RoutedEventArgs args) => await OpenSelectedCandidateAsync();

    private async void CandidateKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || CandidateBox.IsDropDownOpen || !OpenCandidateButton.IsEnabled) return;
        args.Handled = true;
        await OpenSelectedCandidateAsync();
    }

    private async Task OpenSelectedCandidateAsync()
    {
        if (busy || CandidateBox.SelectedItem is not ImplementationCandidate candidate
            || FindDefinitionFile(candidate.TargetFilePath) is not { } file) return;
        await NavigateEntryAsync(file, candidate.TargetMethod, view: DefinitionView.ImplementationCandidate);
    }

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
            history.Clear();
            await SelectMethodAsync(target.Id);
            if (closed) return;
            Editor.Select(offset, 0);
            Editor.ScrollTo(position.Line, position.Column);
        }
        if (NodesForClick(offset, position.Line) is { } node)
            JumpToPreviewNode(node, navigateSource: false);
    }

    private void JumpToPreviewNode(FlowNode node, bool navigateSource, bool keepList = false)
    {
        SelectNode(node, navigateSource);
        if (previewUnavailable || !ready || (keepList && showList))
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

    private static string? DefinitionPath(FlowNode node) => node.TargetMethod is not null
        ? node.TargetFilePath : node.TargetDeclaration?.FilePath;

    private ProjectFile? FindTargetFile(FlowNode node) => FindDefinitionFile(DefinitionPath(node));

    private ProjectFile? FindDefinitionFile(string? path) => path is not null
        ? project?.Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))
        : currentFile;

    private IEnumerable<FlowTarget> AvailableTargets(FlowNode node) =>
        (node.CallTargets ?? []).Where(target => FindDefinitionFile(target.FilePath) is not null);

    private bool CanFollow(FlowNode node) =>
        AvailableTargets(node).Any()
        || (node.TargetMethod is not null || node.TargetDeclaration is not null) && FindTargetFile(node) is not null;

    private void ShowNavigationChoices<T>(Button anchor, IEnumerable<T> values,
        Func<T, string> label, Func<T, Task> navigate)
    {
        if (navigationMenu is { } previousMenu) previousMenu.IsOpen = false;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var value in values)
        {
            var item = new MenuItem { Header = label(value) };
            item.Click += async (_, _) => await navigate(value);
            menu.Items.Add(item);
        }
        navigationMenu = menu;
        menu.IsOpen = true;
    }

    private bool CanFindCaller => history.Count > 0
        || currentFile is not null && method is not null && method.Id != -1
            && (!currentFile.IsJavaScript || !method.Name.StartsWith("JavaScript 初期化 ", StringComparison.Ordinal))
            && callerSearch is not { Locations.Count: 0 };

    private async Task FindOrReturnToCallerAsync()
    {
        if (busy || !CanFindCaller) return;
        if (history.TryPeek(out var previous))
        {
            await NavigateEntryAsync(previous.File, previous.Method, previous.Node, previous.Razor, previous.Script, previous.View,
                commitHistory: () => history.Pop());
            return;
        }
        if (project is not { } currentProject || currentFile is not { } targetFile || method is not { } target) return;
        if (callerSearch is null && !await RunNavigationAsync("呼び出し元を検索中…",
            token => Task.Run(() => currentProject.FindCallersAsync(targetFile, target.Id, token), token),
            result => callerSearch = result)) return;

        if (callerSearch is not { } found || closed) return;
        if (found.Locations.Count == 0)
        {
            SetStatus(found.IsPartial ? "確認できた範囲で呼び出し元は見つかりませんでした。"
                : "解析対象のソースに呼び出し元は見つかりませんでした。");
            BackButton.ToolTip = "静的に特定できる呼び出し元が見つかりません。";
            return;
        }
        if (found.Locations.Count == 1 && !found.IsPartial)
            await OpenCallerAsync(found.Locations[0]);
        else
        {
            SetStatus(found.IsPartial ? "確認できた範囲の呼び出し元を表示しています。" : "呼び出し元を選択してください。");
            ShowNavigationChoices(BackButton, found.Locations,
                location => $"{location.File.Label}  ·  L{location.Source.Line}", OpenCallerAsync);
        }
    }

    private async Task OpenCallerAsync(CallerLocation caller)
    {
        if (project is not { } currentProject) return;
        await RunNavigationAsync("呼び出し元の表示を準備中…", async token =>
        {
            var context = caller.RazorContext is { } page
                ? await Task.Run(() => currentProject.ReadRazorAsync(page, token), token) : null;
            return await PrepareNavigationAsync(caller.File, caller.EntryId, context, null, token);
        }, prepared =>
        {
            ApplyPreparedEntry(caller.File, prepared);
            var node = nodes.Values.Where(node => node.Source is { } range && IsCurrentSource(range)
                    && range.Offset <= caller.Source.Offset && caller.Source.Offset - range.Offset < range.Length
                    && node.Kind is NodeKind.Call or NodeKind.Transfer)
                .MinBy(node => node.Source!.Length);
            if (node is not null)
                JumpToPreviewNode(node, navigateSource: false, keepList: true);
            NavigateSource(caller.Source);
            Editor.Focus();
            SetStatus($"呼び出し元へ移動しました · L{caller.Source.Line}");
        });
    }

    private string NavigationHint(FlowNode node)
    {
        if (AvailableTargets(node).Any()) return "移動する関数の定義を選択 (F12)";
        if (CanFollow(node))
            return node.TargetMethod is not null
                ? "選択した呼び出しの定義へ移動 (F12)"
                : node.ImplementationCandidates is { Count: > 0 } ? "宣言へ移動 (F12)。実装候補も確認できます。"
                    : "宣言へ移動 (F12)。表示できる本体はありません。";
        if (node.TargetMethod is not null || node.TargetDeclaration is not null)
            return "移動先のファイルが対象ファイル一覧にありません。";
        if (node.Kind == NodeKind.Condition) return "条件ノードです。移動するには対応する呼び出しノードを選んでください。";
        if (node.RequiresRuntimeDispatch)
            return "実行時に決まるため、呼び出し先を一意に特定できません。";
        return node.Kind == NodeKind.Call || node.Kind == NodeKind.Transfer
            ? "解析対象のソースで対応する定義を一意に特定できません。"
            : "このノードには呼び出し先がありません。";
    }

    private async Task NavigateDeclarationAsync(ProjectFile file, SourceRange declaration, FlowNode caller, Action commitHistory)
    {
        // Candidate paths without a file are relative to the caller's tree, not the declaration's.
        var candidates = caller.ImplementationCandidates?
            .Select(candidate => candidate with
            {
                TargetFilePath = candidate.TargetFilePath ?? currentFile?.Path
            })
            .Where(candidate => FindDefinitionFile(candidate.TargetFilePath) is not null).ToArray() ?? [];
        await RunNavigationAsync("宣言の表示を準備中…",
            token => ReadNavigationSourceAsync(file, null, null, token), loaded =>
        {
            commitHistory();
            ApplyPreparedFile(file, loaded);
            if (diagram is not null) SaveFileState();
            MethodsBox.SelectedItem = null;
            ResetDiagram();
            RefreshDiagnostics();
            SetState("宣言のみ（本体なし）", "この宣言には表示できるフローがありません。宣言のソース位置を表示します。");
            if (candidates.Length > 0)
            {
                CandidateBox.ItemsSource = candidates;
                CandidatePanel.Visibility = Visibility.Visible;
            }
            NavigateSource(declaration);
            Editor.Focus();
            SetStatus("宣言へ移動しました。呼び出し元へ (Alt+←) で戻れます。");
            SaveProjectState();
        });
    }

    private async Task NavigateEntryAsync(ProjectFile file, int entryId, string? returnTo = null,
        RazorSourceFile? razorContext = null, JavaScriptSourceFile? jsContext = null, DefinitionView view = DefinitionView.Direct,
        Action? commitHistory = null)
    {
        if (!busy && currentFile == file && method?.Id == entryId && diagram is not null
            && (jsContext is null || ReferenceEquals(jsContext, javaScriptSource))
            && (razorContext is null || ReferenceEquals(razorContext, razorSource)))
        {
            commitHistory?.Invoke();
            if (returnTo is not null && nodes.TryGetValue(returnTo, out var node))
                JumpToPreviewNode(node, navigateSource: true);
            ShowDefinitionView(view);
            UpdateSurface();
            return;
        }

        await RunNavigationAsync(returnTo is null ? "呼び出し先の表示を準備中…" : "呼び出し元の表示を準備中…",
            token => PrepareNavigationAsync(file, entryId, razorContext, jsContext, token),
            prepared =>
            {
                commitHistory?.Invoke();
                ApplyPreparedEntry(file, prepared, returnTo, view);
            });
    }

    private string EntryTypeLabel => currentFile?.IsRazor == true ? "画面イベント"
        : currentFile?.IsJavaScript == true ? "JavaScript 関数" : "メソッド";

    private void UpdateSourcePresentation()
    {
        SourceLanguage.Text = currentFile?.IsRazor == true ? "Razor"
            : currentFile?.IsJavaScript == true ? "JavaScript" : "C#";
        EntrySearchHint.Text = EntryTypeLabel + "を検索";
        Filter.ToolTip = currentFile?.IsRazor == true ? "イベント名、要素名、または行番号" : "名前、シグネチャ、または行番号";
        AutomationProperties.SetName(Filter, EntrySearchHint.Text);
        AutomationProperties.SetName(MethodsBox, EntryTypeLabel);
        AutomationProperties.SetName(Editor, SourceLanguage.Text + " ソース");
        ApplyTheme();
    }
}
