using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Flow.Analysis;
using Flow.Mermaid;
using Flow.Model;
using Microsoft.Win32;
using ICSharpCode.AvalonEdit.Search;

namespace Flow.Desktop;

public partial class MainWindow : Window
{
    private const int MaxPreviewNodes = 650;
    private const int MaxPreviewEdges = 1000;
    private const int MaxPreviewCharacters = 380_000;
    private readonly PreviewHost preview;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Stack<(ProjectFile File, int Method, string Node, RazorSourceFile? Razor, JavaScriptSourceFile? Script, DefinitionView View)> history = new();
    private readonly List<string> diagnostics = [];
    private ProjectSession? project;
    private SourceFile? source;
    private readonly Dictionary<ProjectFile, SourceFile> csharpSources = new();
    private readonly Dictionary<(SourceFile Source, int Entry), (FlowDiagram Graph, string? Mermaid)> csharpFlows = new();
    private RazorSourceFile? razorSource;
    private JavaScriptSourceFile? javaScriptSource;
    private ProjectFile? currentFile;
    private FlowDiagram? diagram;
    private Dictionary<string, FlowNode> nodes = [];
    private string? path;
    private string? mermaid;
    private MethodEntry? method;
    private FlowNode? selectedNode;
    private bool dark;
    private bool busy;
    private bool ready;
    private bool rendered;
    private bool showList;
    private bool previewUnavailable;
    private bool closed;
    private int version;
    private int diagramRevision;
    private CallExclusions excludedCalls = new([]);

    private IReadOnlyList<MethodEntry> CurrentEntries => source?.Methods ?? razorSource?.Entries ?? javaScriptSource?.Entries ?? [];
    private bool HasSource => source is not null || razorSource is not null || javaScriptSource is not null;

    public MainWindow()
    {
        InitializeComponent();
        SearchPanel.Install(Editor);
        Editor.TextArea.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(EditorPressed), true);
        Editor.TextArea.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(EditorClicked), true);
        dark = Theme.Read();
        ApplyTheme();
        preview = new PreviewHost(Web);
        preview.Message += OnPreviewMessage;
        Loaded += StartPreview;
        Loaded += RestoreProject;
        Closing += (_, _) => { SaveFileState(); SaveProjectState(); };
        Closed += CloseViewer;
        SetBusy(false);
        try { excludedCalls = TraceSettings.Read(TraceSettings.SettingsPath); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            SetStatus("保存した追跡設定を読み込めません。追跡設定から内容を確認してください。", attention: true);
        }
    }

    private async void StartPreview(object sender, RoutedEventArgs args)
    {
        Loaded -= StartPreview;
        try
        {
            await preview.InitializeAsync(lifetime.Token);
            if (closed || previewUnavailable) return;
            ready = true;
            UpdateSurface();
            RequestRender();
        }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException) when (!closed)
        {
            SetPreviewUnavailable("WebView2 Evergreen Runtime x64 が見つかりません。ノード一覧と Mermaid 出力は利用できます。");
        }
        catch (Exception error) when (!closed)
        {
            SetPreviewUnavailable($"WebView2 プレビューを起動できません（{error.GetType().Name} / 0x{error.HResult:X8}）。ノード一覧と Mermaid 出力は利用できます。");
        }
        catch (Exception) when (closed) { }
    }

    private void CloseViewer(object? sender, EventArgs args)
    {
        closed = true;
        lifetime.Cancel();
        project?.Dispose();
        preview.Close();
    }

    private void SetPreviewUnavailable(string message)
    {
        previewUnavailable = true;
        if (diagram is not null) showList = true;
        RefreshDiagnostics();
        if (!diagnostics.Contains(message)) diagnostics.Add(message);
        DiagnosticsBox.Visibility = Visibility.Visible;
        UpdateDiagnosticList();
        UpdateSurface();
    }

    private async void OpenProject(object? sender, RoutedEventArgs args)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Title = "C# プロジェクトを開く", Filter = "C# プロジェクト (*.csproj)|*.csproj", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await OpenProjectAsync(dialog.FileName);
    }

    private async void ReloadProject(object sender, RoutedEventArgs args)
    {
        if (busy || path is null) return;
        var previousFile = (FilesBox.SelectedItem as ProjectFile)?.Path;
        var previousOptions = SettingsBox.SelectedItem as ProjectOptions;
        SetBusy(true);
        try
        {
            var settings = await ProjectLoader.InspectAsync(path, lifetime.Token);
            if (closed) return;
            SdkLabel.Text = ".NET SDK " + settings.SdkVersion;
            SettingsBox.ItemsSource = settings.Options;
            var options = settings.Options.FirstOrDefault(item => item == previousOptions) ?? settings.Options[0];
            SettingsBox.SelectedItem = options;
            await LoadProject(options, previousFile);
            if (project is not null) SaveProjectState();
        }
        catch (Exception exception) when (!closed) { ShowFailure(exception, "プロジェクトを再読み込みできません"); }
        catch (Exception) when (closed) { }
        finally { if (!closed) SetBusy(false); }
    }

    private async void ProjectOptionsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (busy || path is null || SettingsBox.SelectedItem is not ProjectOptions options) return;
        SetBusy(true);
        try { await LoadProject(options, (FilesBox.SelectedItem as ProjectFile)?.Path); if (project is not null) SaveProjectState(); }
        catch (Exception exception) when (!closed) { ShowFailure(exception, "構成を読み込めません"); }
        catch (Exception) when (closed) { }
        finally { if (!closed) SetBusy(false); }
    }

    private async Task LoadProject(ProjectOptions options, string? previousFile = null)
    {
        ResetProject();
        SetState("プロジェクトを読み込み中", "MSBuild でソースと参照情報を取得しています…");
        SetStatus(options + " の設計時ビルドを読み込み中");
        var loaded = await ProjectLoader.OpenAsync(path!, options, lifetime.Token);
        if (closed) { loaded.Dispose(); return; }
        project = loaded;
        FilesBox.ItemsSource = loaded.Files;
        CollectionViewSource.GetDefaultView(loaded.Files).Filter =
            item => item is ProjectFile file && file.Label.Contains(FileFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        UpdateFileCount();
        RefreshDiagnostics();
        if (loaded.Files.Count == 0)
        {
            SetState("対象ファイルがありません", "構成、対象フレームワーク、Compile 項目と診断を確認してください。");
            SetStatus("選択できる C# / Razor / JavaScript ファイルがありません。", attention: true);
            return;
        }
        if (loaded.Files.FirstOrDefault(file => file.Path == previousFile) is { } previous)
        {
            if (!FilesBox.Items.Contains(previous)) FileFilter.Clear();
            FilesBox.SelectedItem = previous;
            await LoadFile(previous);
            return;
        }
        SetState("対象ファイルを選択", "左の一覧から .cs、.cshtml.cs、または .cshtml を選んでください。");
        SetStatus(loaded.Notices.Count == 0 ? "対象ファイルを選択してください。" : "MSBuild 診断があります。対象ファイルを選択できます。",
            attention: loaded.Notices.Count != 0);
    }

    private async void FileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (busy || project is null || FilesBox.SelectedItem is not ProjectFile file) return;
        SetBusy(true);
        try { await LoadFile(file); if (HasSource) SaveProjectState(); }
        catch (Exception exception) when (!closed) { ShowFailure(exception, "ソースを解析できません"); }
        catch (Exception) when (closed) { }
        finally { if (!closed) SetBusy(false); }
    }

    private async Task LoadFile(ProjectFile file, bool preserveHistory = false, bool restoreSelection = true,
        RazorSourceFile? razorContext = null, JavaScriptSourceFile? jsContext = null)
    {
        ResetFile(preserveHistory);
        var loading = file.IsRazor ? "Razor ページを解析中"
            : file.IsJavaScript ? "JavaScript の宣言と参照を解析中"
            : "コンパイルとセマンティックモデルを取得中";
        SetState("ソースを解析中", loading);
        SetStatus(loading);
        var current = project!;
        if (file.IsRazor)
            razorSource = await Task.Run(() => current.ReadRazorAsync(file, lifetime.Token), lifetime.Token);
        else if (file.IsJavaScript)
            javaScriptSource = await Task.Run(() => current.ReadJavaScriptAsync(file, lifetime.Token, razorContext, jsContext), lifetime.Token);
        else if (!csharpSources.TryGetValue(file, out source))
        {
            source = await Task.Run(() => current.ReadAsync(file, lifetime.Token), lifetime.Token);
            if (closed) return;
            if (csharpSources.Count >= 8)
            {
                var first = csharpSources.First();
                csharpSources.Remove(first.Key);
                foreach (var key in csharpFlows.Keys.Where(key => ReferenceEquals(key.Source, first.Value)).ToArray())
                    csharpFlows.Remove(key);
            }
            csharpSources[file] = source;
        }
        if (closed) return;
        currentFile = file;
        Editor.Text = source?.Text ?? razorSource?.Text ?? javaScriptSource!.Text;
        var restored = ReadFileState(file);
        SourceTitle.Text = file.Label;
        SourceTitle.ToolTip = file.Label;
        UpdateSourcePresentation();
        MethodsBox.ItemsSource = CurrentEntries;
        CollectionViewSource.GetDefaultView(MethodsBox.ItemsSource).Filter =
            entry => entry is MethodEntry item && item.ToString().Contains(Filter.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        UpdateMethodCount();
        RefreshDiagnostics();
        SetState(CurrentEntries.Count > 0 ? "解析対象を選択" : "解析対象が見つかりません",
            CurrentEntries.Count > 0 ? "左の一覧で解析対象を選ぶと、フローを表示します。"
                : "ほかのファイルを選ぶか、構成と診断を確認してください。");
        var notices = source?.Notices ?? razorSource?.Notices ?? javaScriptSource!.Notices;
        SetStatus(notices.Any(IsPartial) ? "部分解析 · 診断で解析範囲を確認できます。" : "ソースの読み込み完了",
            attention: notices.Any(IsPartial));
        if (!restoreSelection) return;
        if (restored is not null && FindSavedMethod(CurrentEntries, restored) is { } entry)
        {
            MethodsBox.SelectedItem = entry;
            MethodsBox.ScrollIntoView(entry);
            await AnalyzeMethodAsync(entry, restored: restored);
        }
        else if ((file.IsRazor || file.IsJavaScript) && CurrentEntries.FirstOrDefault() is { } overview)
        {
            MethodsBox.SelectedItem = overview;
            await AnalyzeMethodAsync(overview, restored: restored);
        }
        else if (restored is not null)
            RestoreFileState(restored);
    }

    private async void MethodChanged(object sender, SelectionChangedEventArgs args)
    {
        if (busy || MethodsBox.SelectedItem is not MethodEntry entry) return;
        await AnalyzeMethodAsync(entry);
    }

    private async void EditTraceSettings(object sender, RoutedEventArgs args)
    {
        if (busy) return;
        var dialog = new TraceSettingsDialog(this, excludedCalls);
        if (dialog.ShowDialog() != true) return;
        excludedCalls = dialog.ExcludedCalls;
        csharpFlows.Clear();
        if (method is { } current)
            await AnalyzeMethodAsync(current);
        else
            SetStatus("追跡設定を保存しました。");
    }

    private async Task AnalyzeMethodAsync(MethodEntry entry, string? returnTo = null, FileViewState? restored = null)
    {
        var currentSource = source;
        var currentRazor = razorSource;
        var currentScript = javaScriptSource;
        if (currentSource is null && currentRazor is null && currentScript is null) return;
        SetBusy(true);
        var exclusions = excludedCalls;
        try
        {
            ResetDiagram();
            method = entry;
            FlowTitle.Text = entry.Signature;
            FlowTitle.ToolTip = entry.Signature;
            NavigateSource(entry.Source with { Length = 0 });
            var loading = currentRazor is not null ? "画面イベントと送信先を解析中"
                : currentScript is not null ? "JavaScript の制御フローを解析中" : "Roslyn CFG から図を組み立てています…";
            SetState("フローを解析中", loading);
            SetStatus(loading);
            if (currentSource is null || !csharpFlows.TryGetValue((currentSource, entry.Id), out var flow))
            {
                flow = await Task.Run(() =>
                {
                    var graph = currentSource is not null
                        ? currentSource.Analyze(entry.Id, lifetime.Token, exclusions)
                        : currentRazor is not null ? currentRazor.Analyze(entry.Id, lifetime.Token)
                        : currentScript!.Analyze(entry.Id, lifetime.Token);
                    return (Graph: graph, Mermaid: graph.Nodes.Count > 0 ? MermaidWriter.Write(graph) : null);
                }, lifetime.Token);
                if (closed) return;
                if (currentSource is not null && flow.Graph.Nodes.Count <= 4_000 && flow.Graph.Edges.Count <= 8_000
                    && (flow.Mermaid?.Length ?? 0) <= 350_000)
                {
                    if (csharpFlows.Count >= 16) csharpFlows.Remove(csharpFlows.Keys.First());
                    csharpFlows[(currentSource, entry.Id)] = flow;
                }
            }
            var result = flow.Graph;
            diagram = result;
            nodes = result.Nodes.ToDictionary(node => node.Id);
            NodesBox.ItemsSource = result.Nodes;
            mermaid = flow.Mermaid;
            UpdateAnnotationKey();
            showList = previewUnavailable;
            SetState("フロー図を取得できません", "診断を確認するか、別の解析対象を選んでください。");
            RefreshDiagnostics();
            UpdateSurface();
            if (returnTo is not null && nodes.TryGetValue(returnTo, out var caller))
                JumpToPreviewNode(caller, navigateSource: true);
            if (restored is not null)
                RestoreFileState(restored);
            RequestRender();
            ShowAnnotations();
            SetStatus(result.IsPartial ? "部分解析 · 診断を確認してください。"
                : $"{result.Nodes.Count:N0} ノード  /  {result.Edges.Count:N0} 遷移" +
                    (currentSource is not null ? "  ·  単純な代入値は図から省略"
                        : currentRazor is not null ? "  ·  画面イベントと送信先" : ""), result.IsPartial);
        }
        catch (Exception exception) when (!closed) { ShowFailure(exception, "フロー図を取得できません"); }
        catch (Exception) when (closed) { }
        finally { if (!closed) SetBusy(false); }
    }

    private void ResetProject()
    {
        project?.Dispose();
        project = null;
        csharpSources.Clear();
        csharpFlows.Clear();
        FilesBox.ItemsSource = null;
        FileCount.Text = "対象ファイル";
        ResetFile();
    }

    private void ResetFile(bool preserveHistory = false)
    {
        SaveFileState();
        fileStatePath = null;
        annotationHistory.Clear();
        source = null;
        razorSource = null;
        javaScriptSource = null;
        currentFile = null;
        MethodsBox.ItemsSource = null;
        MethodCount.Text = "メソッド";
        Filter.Clear();
        if (!preserveHistory) history.Clear();
        Editor.Clear();
        GoToPanel.Visibility = Visibility.Collapsed;
        SourceTitle.Text = "ソース";
        SourceTitle.ToolTip = null;
        SourceLocation.Text = "—";
        UpdateSourcePresentation();
        ResetDiagram();
        RefreshDiagnostics();
    }

    private void ResetDiagram()
    {
        ++version;
        ++diagramRevision;
        ZoomLabel.Text = "—";
        diagram = null;
        method = null;
        mermaid = null;
        AnnotationKey = null;
        viewport = null;
        nodes.Clear();
        NodesBox.ItemsSource = null;
        selectedNode = null;
        rendered = false;
        pendingNodeCenter = false;
        grayCalculation = null;
        appliedGray = null;
        appliedMarks = null;
        editorAnnotations.Clear();
        Editor.TextArea.TextView.Redraw();
        ShowDefinitionView(DefinitionView.Direct);
        ClearDeclarationCandidates();
        NodeLabel.Text = "ノードを選ぶとソース行へ移動";
        NodeLabel.ToolTip = null;
        NavigationStatus.Text = "";
        NavigationStatus.Visibility = Visibility.Collapsed;
        FollowButton.ToolTip = "呼び出しノードを選択してください。";
        FlowTitle.Text = "制御フロー";
        FlowTitle.ToolTip = null;
        UpdateSurface();
    }

    private void FileFilterChanged(object sender, TextChangedEventArgs args)
    {
        if (FilesBox?.ItemsSource is not { } files) return;
        CollectionViewSource.GetDefaultView(files).Refresh();
        UpdateFileCount();
    }

    private void UpdateFileCount() =>
        FileCount.Text = $"対象ファイル  ·  {FilesBox.Items.Count:N0} / {project?.Files.Count ?? 0:N0}";

    private void FilterChanged(object sender, TextChangedEventArgs args)
    {
        if (MethodsBox?.ItemsSource is not { } entries) return;
        var retained = MethodsBox.SelectedItem;
        var view = CollectionViewSource.GetDefaultView(entries);
        view.Refresh();
        if (retained is not null && view.Contains(retained))
            MethodsBox.SelectedItem = retained;
        UpdateMethodCount();
    }

    private void UpdateMethodCount() => MethodCount.Text =
        $"{EntryTypeLabel}  ·  {MethodsBox.Items.Count:N0} / {CurrentEntries.Count:N0}";

    private void NodeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!selectingFromSource && NodesBox.SelectedItem is FlowNode node)
            SelectNode(node);
    }

    private void SelectNode(FlowNode node, bool navigateSource = true)
    {
        selectedNode = node;
        NodeLabel.Text = node.Label + (node.IsReachable ? "" : "  ·  到達不能");
        var navigation = NavigationHint(node);
        NodeLabel.ToolTip = $"{NodeLabel.Text}\n{navigation}";
        FollowButton.ToolTip = navigation;
        NavigationStatus.Text = CanFollow(node) && DefinitionPath(node) is { } targetPath
            ? $"{(node.TargetMethod is null ? "宣言のみ" : node.RequiresRuntimeDispatch ? "宣言のフロー" : "定義")}: {Path.GetFileName(targetPath)}" : navigation;
        NavigationStatus.ToolTip = navigation;
        NavigationStatus.Visibility = Visibility.Visible;
        if (navigateSource && node.Source is { } range) NavigateSource(range);
        FollowButton.IsEnabled = !busy && CanFollow(node);
    }

    private void NavigateSource(SourceRange location)
    {
        if (!IsCurrentSource(location)) return;
        var offset = Math.Clamp(location.Offset, 0, Editor.Document.TextLength);
        var length = Math.Clamp(location.Length, 0, Editor.Document.TextLength - offset);
        var position = Editor.Document.GetLocation(offset);
        Editor.Select(offset, length);
        Editor.ScrollTo(position.Line, position.Column);
        SourceLocation.Text = $"L{position.Line}  /  {Editor.LineCount:N0} 行";
    }

    private async void Follow(object sender, RoutedEventArgs args) => await FollowSelectedAsync();

    private async Task FollowSelectedAsync()
    {
        if (busy || selectedNode is not { } caller || method is not { } entry || currentFile is not { } file
            || !CanFollow(caller) || FindTargetFile(caller) is not { } targetFile) return;
        history.Push((file, entry.Id, caller.Id, razorSource, javaScriptSource, definitionView));
        if (caller.TargetMethod is not { } target)
        {
            await NavigateDeclarationAsync(targetFile, caller.TargetDeclaration!, caller);
            return;
        }
        var view = caller.RequiresRuntimeDispatch ? DefinitionView.RuntimeDeclaration : DefinitionView.Direct;
        if (targetFile == file && target == entry.Id)
        {
            NavigateSource(entry.Source with { Length = 0 });
            if (diagram?.Nodes.FirstOrDefault(node => node.Kind == NodeKind.Start) is { } start)
                JumpToPreviewNode(start, navigateSource: false, keepList: true);
            ShowDefinitionView(view);
            SetStatus(caller.RequiresRuntimeDispatch ? "宣言の開始位置へ移動しました。実装は実行時に決まります。"
                : "自己再帰: 同じメソッドの開始位置へ移動しました。");
            SetBusy(false);
            return;
        }
        await NavigateEntryAsync(targetFile, target, razorContext: razorSource, jsContext: javaScriptSource, view: view);
    }

    private async void Back(object? sender, RoutedEventArgs args)
    {
        if (busy || !history.TryPop(out var previous)) return;
        await NavigateEntryAsync(previous.File, previous.Method, previous.Node, previous.Razor, previous.Script, previous.View);
    }

    private async Task SelectMethodAsync(int id, string? returnTo = null)
    {
        if (CurrentEntries.FirstOrDefault(item => item.Id == id) is not { } entry)
        {
            SetStatus("移動先の解析対象が見つかりません。プロジェクトを再読み込みしてください。", attention: true);
            return;
        }
        if (method?.Id == id && diagram is not null)
        {
            if (returnTo is not null && nodes.TryGetValue(returnTo, out var caller))
                JumpToPreviewNode(caller, navigateSource: true);
            SetBusy(false);
            return;
        }

        SetBusy(true);
        if (!MethodsBox.Items.Contains(entry)) Filter.Clear();
        MethodsBox.SelectedItem = entry;
        MethodsBox.ScrollIntoView(entry);
        await AnalyzeMethodAsync(entry, returnTo);
    }

    private void Copy(object sender, RoutedEventArgs args)
    {
        if (mermaid is null) return;
        try { Clipboard.SetText(mermaid); SetStatus("Mermaid をクリップボードにコピーしました。"); }
        catch (ExternalException) { SetStatus("クリップボードを使用できません。時間をおいて再試行してください。", true); }
    }

    private void Save(object sender, RoutedEventArgs args)
    {
        if (mermaid is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Mermaid (*.mmd)|*.mmd", DefaultExt = ".mmd", AddExtension = true, FileName = "flow.mmd",
            Title = "Mermaid 図を保存"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, mermaid); SetStatus("Mermaid 図を保存しました。"); }
        catch (IOException) { SetStatus("保存できません。保存先のファイルと空き容量を確認してください。", true); }
        catch (UnauthorizedAccessException) { SetStatus("保存先への書き込み権限を確認してください。", true); }
    }

    private void ToggleTheme(object sender, RoutedEventArgs args)
    {
        dark = !dark;
        ApplyTheme();
        if (!Theme.Save(dark)) SetStatus("テーマ設定を保存できません。", true);
        RequestRender(preserveViewport: true);
    }

    private void ApplyTheme()
    {
        Theme.Apply(this, Editor, dark, currentFile?.IsRazor == true ? "HTML"
            : currentFile?.IsJavaScript == true ? "JavaScript" : "C#");
        var transformers = Editor.TextArea.TextView.LineTransformers;
        transformers.Remove(editorAnnotations);
        editorAnnotations.Dark = dark;
        transformers.Add(editorAnnotations);
        Editor.TextArea.TextView.Redraw();
        ThemeButton.Content = dark ? "◐  ダーク" : "☼  ライト";
    }

    private void OnPreviewMessage(string type, JsonElement value, int requestVersion)
    {
        if (closed) return;
        if (type == "process-error")
        {
            SetPreviewUnavailable("WebView2 のプロセスを利用できません。再起動後に再試行できます。");
            return;
        }
        if (requestVersion != version) return;
        switch (type)
        {
            case "viewport" when value.Deserialize<PreviewViewport>() is { IsValid: true } saved:
                viewport = saved;
                break;
            case "zoom":
                ZoomLabel.Text = $"{value.GetDouble():0.###}%";
                break;
            case "node" when value.GetString() is { } id && nodes.TryGetValue(id, out var node):
                SelectNode(node);
                break;
            case "back":
                Back(this, new RoutedEventArgs());
                break;
            case "annotate" when rendered:
                ChangeAnnotation(value);
                break;
            case "error":
                showList = true;
                rendered = false;
                UpdateSurface();
                diagnostics.Add("Mermaid プレビューの描画に失敗しました。ノード一覧と出力を利用できます。");
                UpdateDiagnosticList();
                SetStatus("Mermaid の描画に失敗しました。", true);
                break;
            case "rendered":
                rendered = true;
                UpdateSurface();
                if (selectedNode is { } selected)
                    preview.Send(new { type = "select", id = selected.Id, center = pendingNodeCenter });
                pendingNodeCenter = false;
                ShowAnnotations();
                break;
        }
    }

    private void RequestRender(bool preserveViewport = false)
    {
        if (!ready || previewUnavailable || mermaid is null || diagram is not { } graph) return;
        var useMermaid = !UseLargeLayout(graph);
        var text = useMermaid ? MermaidWriter.Write(graph, light: !dark, includeSourceLines: false) : null;
        rendered = false;
        UpdateSurface();
        ++version;
        preview.Send(new
        {
            type = "render", version, text, preserveViewport, diagramRevision, viewport,
            theme = dark ? "dark" : "light",
            nodes = graph.Nodes.Select(node => new
            {
                id = node.Id, label = node.Label, kind = node.Kind.ToString().ToLowerInvariant(), reachable = node.IsReachable
            }),
            edges = graph.Edges.Select(edge => new
            {
                from = edge.From, to = edge.To, label = edge.Label, reachable = edge.IsReachable
            })
        });
    }

    private bool UseLargeLayout(FlowDiagram graph) => graph.Nodes.Count > MaxPreviewNodes
        || graph.Edges.Count > MaxPreviewEdges || mermaid?.Length > MaxPreviewCharacters;

    private void ToggleList(object sender, RoutedEventArgs args)
    {
        showList = !showList;
        UpdateSurface();
        if (showList && selectedNode is { } node)
        {
            NodesBox.SelectedItem = node;
            NodesBox.ScrollIntoView(node);
        }
        else if (selectedNode is { } selected)
            preview.Send(new { type = "select", id = selected.Id, center = true });
    }

    private void FindInPreview(object sender, RoutedEventArgs args)
    {
        if (!rendered) return;
        showList = false;
        UpdateSurface();
        Web.Focus();
        preview.Send(new { type = "search" });
    }

    private void Fit(object sender, RoutedEventArgs args) => preview.Send(new { type = "fit" });
    private void Zoom(object sender, RoutedEventArgs args) => preview.Send(new { type = "zoom", factor = sender == ZoomIn ? 1.18 : 1 / 1.18 });

    private void SetBusy(bool value)
    {
        busy = value;
        OpenButton.IsEnabled = !value;
        ReloadButton.IsEnabled = !value && path is not null;
        TraceSettingsButton.IsEnabled = !value;
        SettingsBox.IsEnabled = !value && SettingsBox.Items.Count > 1;
        BrowserPane.IsEnabled = !value;
        OpenCandidateButton.IsEnabled = !value && CandidateBox.SelectedItem is ImplementationCandidate;
        CandidateBox.IsEnabled = !value;
        UpdateSurface();
    }

    private void UpdateSurface()
    {
        var hasGraph = mermaid is not null;
        var webVisible = hasGraph && ready && !showList && !previewUnavailable;
        var listVisible = hasGraph && (showList || !ready || previewUnavailable);
        EmptyState.Visibility = !webVisible && !listVisible ? Visibility.Visible : Visibility.Collapsed;
        // The composition control needs a layout pass to initialize before it can signal readiness.
        PreviewSurface.Opacity = webVisible ? 1 : 0;
        PreviewSurface.IsHitTestVisible = Web.Focusable = webVisible && rendered;
        NodesBox.Visibility = listVisible ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = SaveButton.IsEnabled = hasGraph && !busy;
        BackButton.IsEnabled = !busy && history.Count > 0;
        FollowButton.IsEnabled = !busy && selectedNode is { } node && CanFollow(node);
        ListButton.Content = showList ? "図" : "一覧";
        ListButton.IsEnabled = hasGraph && ready && !previewUnavailable;
        AnnotationsButton.IsEnabled = hasGraph && !busy;
        PreviewSearchButton.IsEnabled = webVisible && rendered;
        ZoomIn.IsEnabled = ZoomOut.IsEnabled = FitButton.IsEnabled = webVisible && rendered;
    }

    private void SetState(string title, string help)
    {
        EmptyTitle.Text = title;
        EmptyHelp.Text = help;
        UpdateSurface();
    }

    private void SetStatus(string message, bool attention = false)
    {
        StatusText.Text = message;
        StatusDot.SetResourceReference(TextBlock.ForegroundProperty, attention ? "WarningBrush" : "GoodBrush");
    }

    private void ShowFailure(Exception exception, string title)
    {
        var hint = exception is ProjectLoadException ? exception.Message
            : "SDK、復元済み依存関係、プロジェクト設定を確認してください。";
        var message = title + " · " + hint;
        SetState(title, hint);
        SetStatus(message, true);
        diagnostics.Add(message);
        UpdateDiagnosticList();
    }

    private static bool IsPartial(string notice) => notice.StartsWith("解析は部分的", StringComparison.Ordinal);

    private void RefreshDiagnostics()
    {
        var previewIssue = diagnostics.FirstOrDefault(text => text.StartsWith("WebView2", StringComparison.Ordinal));
        diagnostics.Clear();
        if (project is not null) diagnostics.AddRange(project.Notices);
        if (source is not null) diagnostics.AddRange(source.Notices);
        if (razorSource is not null) diagnostics.AddRange(razorSource.Notices);
        if (javaScriptSource is not null) diagnostics.AddRange(javaScriptSource.Notices);
        if (diagram is not null) diagnostics.AddRange(diagram.Notices);
        if (previewIssue is not null) diagnostics.Add(previewIssue);
        if (diagram is { } graph && UseLargeLayout(graph))
            diagnostics.Add("大きな図を軽量レイアウトで表示します。全体の Mermaid は .mmd に保存できます。");
        UpdateDiagnosticList();
    }

    private void UpdateDiagnosticList()
    {
        DiagnosticsBox.ItemsSource = diagnostics.Distinct().ToArray();
        DiagnosticButton.Content = $"診断  ·  {DiagnosticsBox.Items.Count}";
    }

    private void ShowLicenses(object sender, RoutedEventArgs args)
    {
        const string prefix = "Notices.";
        var assembly = typeof(MainWindow).Assembly;
        var choices = new ComboBox { Margin = new Thickness(0, 0, 0, 8),
            ItemsSource = assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(name => name[prefix.Length..]).Order(StringComparer.Ordinal).ToArray() };
        var text = new RichTextBox { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        text.SetResourceReference(BackgroundProperty, "CanvasBrush");
        text.SetResourceReference(ForegroundProperty, "TextBrush");
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(choices, Dock.Top);
        panel.Children.Add(choices);
        panel.Children.Add(text);
        var window = new Window { Owner = this, Title = "ライセンスと第三者通知", Width = 730, Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        foreach (var key in Resources.Keys)
            window.Resources[key] = Resources[key];
        window.SetResourceReference(BackgroundProperty, "WindowBrush");
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedItem is not string item) return;
            using var stream = assembly.GetManifestResourceStream(prefix + item)!;
            text.Document.Blocks.Clear();
            var document = new TextRange(text.Document.ContentStart, text.Document.ContentEnd);
            if (item.EndsWith(".rtf", StringComparison.Ordinal))
                document.Load(stream, DataFormats.Rtf);
            else
            {
                using var reader = new StreamReader(stream);
                document.Text = reader.ReadToEnd();
            }
            text.ScrollToHome();
        };
        choices.SelectedItem = "THIRD-PARTY-NOTICES.md";
        window.ShowDialog();
    }

    private void ToggleDiagnostics(object sender, RoutedEventArgs args) =>
        DiagnosticsBox.Visibility = DiagnosticsBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        if (args.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            args.Handled = true;
            OpenProject(this, args);
        }
        else if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && Web.IsKeyboardFocusWithin)
        {
            args.Handled = true;
            FindInPreview(this, args);
        }
        else if (args.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control
            && (Editor.IsKeyboardFocusWithin || GoToPanel.IsKeyboardFocusWithin))
        {
            args.Handled = true;
            OpenGoToLine();
        }
        else if (args.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None && FollowButton.IsEnabled)
        {
            args.Handled = true;
            Follow(this, args);
        }
        else if (args.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            args.Handled = true;
            Back(this, args);
        }
        base.OnPreviewKeyDown(args);
    }
}
