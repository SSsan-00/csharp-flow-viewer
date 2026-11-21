using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Flow.Analysis;

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

    private async Task ChangeProjectAsync(string projectPath, ProjectOptions? requested, string? previousFile, bool inspect = true)
    {
        ProjectSession? pending = null;
        try
        {
            var applied = await RunOperationAsync("SDK とプロジェクト設定を確認中…", async token =>
            {
                var settings = inspect ? await ProjectLoader.InspectAsync(projectPath, token) : null;
                var options = settings is null ? requested! : settings.Options.FirstOrDefault(item => item == requested) ?? settings.Options[0];
                SetStatus(options + " の設計時ビルドを読み込み中…");
                pending = await ProjectLoader.OpenAsync(projectPath, options, token);
                return (settings, options, session: pending);
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
            }, "プロジェクトを読み込めません。", "project");
            if (closed) return;
            if (!applied)
            {
                RestoreSelection(() => SettingsBox.SelectedItem = project?.Options);
                return;
            }
            SaveProjectState();
            if (previousFile is not null && project?.Files.FirstOrDefault(file =>
                string.Equals(file.Path, previousFile, StringComparison.OrdinalIgnoreCase)) is { } previous)
            {
                if (!FilesBox.Items.Contains(previous)) FileFilter.Clear();
                await LoadFileAsync(previous);
            }
        }
        finally { pending?.Dispose(); }
    }
}
