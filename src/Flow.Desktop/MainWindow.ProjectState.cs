using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Flow.Analysis;
using Flow.Model;

namespace Flow.Desktop;

public partial class MainWindow
{
    private sealed record ProjectViewState(string Project, string? Configuration, string? TargetFramework, string? File);
    private static readonly string ProjectStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "csharp-flow-viewer", "last-project.json");

    private void SaveProjectState()
    {
        if (project is null || path is null) return;
        var state = new ProjectViewState(path, project.Options.Configuration, project.Options.TargetFramework,
            currentFile?.Path);
        var temporary = ProjectStatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectStatePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, ProjectStatePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus("プロジェクトの表示状態を保存できません。", attention: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async void RestoreProject(object sender, RoutedEventArgs args)
    {
        Loaded -= RestoreProject;
        ProjectViewState? state;
        try
        {
            if (!File.Exists(ProjectStatePath)) return;
            state = JsonSerializer.Deserialize<ProjectViewState>(File.ReadAllText(ProjectStatePath));
            if (state is null || string.IsNullOrWhiteSpace(state.Project)) throw new JsonException();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus("前回のプロジェクト情報を読み込めません。プロジェクトを選択してください。", attention: true);
            return;
        }
        await OpenProjectAsync(state.Project, state);
    }

    private Task OpenProjectAsync(string projectPath, ProjectViewState? state = null) =>
        ChangeProjectAsync(projectPath,
            state is { Configuration: { } configuration } ? new(configuration, state.TargetFramework) : null,
            state?.File);

    private async Task ChangeProjectAsync(string projectPath, ProjectOptions? requested, string? previousFile,
        bool inspect = true, bool reload = false)
    {
        var view = reload && HasSource ? CaptureFileState() with { Annotations = new(annotationHistory) } : null;
        ProjectSession? pending = null;
        try
        {
            var applied = await RunOperationAsync("SDK とプロジェクト設定を確認中…", async token =>
            {
                var settings = inspect ? await ProjectLoader.InspectAsync(projectPath, token) : null;
                var options = settings is null ? requested! : settings.Options.FirstOrDefault(item => item == requested) ?? settings.Options[0];
                SetStatus(options + " の設計時ビルドを読み込み中…");
                pending = await ProjectLoader.OpenAsync(projectPath, options, token);
                var file = reload && previousFile is not null
                    ? pending.Files.FirstOrDefault(item => string.Equals(item.Path, previousFile, StringComparison.OrdinalIgnoreCase))
                    : null;
                PreparedNavigation? prepared = null;
                if (file is not null)
                {
                    SetStatus("再読み込みするソースとフローを解析中…");
                    prepared = await PrepareReloadedFileAsync(pending, file, view, token);
                }
                return (settings, options, session: pending, file, prepared);
            }, result =>
            {
                ResetProject();
                if (path != projectPath) FileFilter.Clear();
                path = projectPath;
                ProjectLabel.Text = Path.GetFileName(projectPath);
                ProjectLabel.ToolTip = projectPath;
                if (result.settings is { } settings)
                {
                    SdkLabel.Text = ".NET SDK " + settings.SdkVersion;
                    SettingsBox.ItemsSource = settings.Options;
                }
                SettingsBox.SelectedItem = result.options;
                project = result.session;
                pending = null;
                FilesBox.ItemsSource = project.Files;
                CollectionViewSource.GetDefaultView(project.Files).Filter =
                    item => item is ProjectFile file && file.Label.Contains(FileFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase);
                UpdateFileCount();
                RefreshDiagnostics();
                SetState(project.Files.Count == 0 ? "対象ファイルがありません" : "対象ファイルを選択",
                    project.Files.Count == 0 ? "構成、対象フレームワーク、Compile 項目と診断を確認してください。"
                        : "左の一覧から C# / Razor / JavaScript ファイルを選んでください。");
                SetStatus(project.Files.Count == 0 ? "選択できるソースファイルがありません。"
                    : project.Notices.Count == 0 ? "対象ファイルを選択してください。" : "MSBuild 診断があります。対象ファイルを選択できます。",
                    attention: project.Files.Count == 0 || project.Notices.Count != 0);
                if (result.file is { } file && result.prepared is { } prepared)
                {
                    if (!FilesBox.Items.Contains(file)) FileFilter.Clear();
                    ApplySelectedFile(file, prepared, GetFileStatePath(file), view);
                    FilesBox.ScrollIntoView(file);
                }
                else if (reload && previousFile is not null)
                    SetStatus("再読み込みしましたが、以前のファイルは解析対象にありません。", attention: true);
            }, reload ? "再読み込みを完了できませんでした。" : "プロジェクトを読み込めません。",
                reload ? "reload" : "project", view is null ? "" : " ソースとフローは前回の表示のままです。");
            if (closed) return;
            if (!applied)
            {
                RestoreSelection(() => SettingsBox.SelectedItem = project?.Options);
                return;
            }
            SaveProjectState();
            if (!reload && previousFile is not null && project?.Files.FirstOrDefault(file =>
                string.Equals(file.Path, previousFile, StringComparison.OrdinalIgnoreCase)) is { } previous)
            {
                if (!FilesBox.Items.Contains(previous)) FileFilter.Clear();
                await LoadFileAsync(previous);
            }
        }
        finally { pending?.Dispose(); }
    }

    private async Task<PreparedNavigation> PrepareReloadedFileAsync(ProjectSession session, ProjectFile file,
        FileViewState? view, CancellationToken token)
    {
        var loaded = await ReadReloadedSourceAsync(session, file, token);
        var entry = view is null ? null : FindSavedMethod(loaded.Entries, view);
        entry ??= file.IsRazor || file.IsJavaScript ? loaded.Entries.FirstOrDefault() : null;
        var flow = entry is null ? null : await PrepareFlowAsync(loaded, entry.Id, token);
        return new(loaded, entry, flow);
    }

    private static async Task<LoadedSource> ReadReloadedSourceAsync(ProjectSession session, ProjectFile file, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await Task.Run(async () =>
                {
                    // Roslyn can return an empty document after an unreadable source file.
                    // Check the current file before applying any new view.
                    using (File.OpenRead(file.Path)) { }
                    return file.IsRazor ? new LoadedSource(null, await session.ReadRazorAsync(file, token), null)
                        : file.IsJavaScript ? new(null, null, await session.ReadJavaScriptAsync(file, token))
                        : new(await session.ReadAsync(file, token), null, null);
                }, token);
            }
            catch (IOException error) when (attempt < 4 && (error.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(150, token);
            }
        }
    }
}
