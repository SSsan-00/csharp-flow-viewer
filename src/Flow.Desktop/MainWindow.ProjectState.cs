using System.IO;
using System.Text.Json;
using System.Windows;
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
            (FilesBox.SelectedItem as ProjectFile)?.Path);
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

    private async Task OpenProjectAsync(string projectPath, ProjectViewState? state = null)
    {
        SetBusy(true);
        try
        {
            ResetProject();
            path = projectPath;
            FileFilter.Clear();
            ProjectLabel.Text = Path.GetFileName(path);
            ProjectLabel.ToolTip = path;
            SdkLabel.Text = "";
            SettingsBox.ItemsSource = null;
            SetState("プロジェクトを評価中", "SDK と構成を確認しています…");
            SetStatus("SDK とプロジェクト設定を確認中");
            var settings = await ProjectLoader.InspectAsync(path, lifetime.Token);
            if (closed) return;
            SdkLabel.Text = ".NET SDK " + settings.SdkVersion;
            SettingsBox.ItemsSource = settings.Options;
            var options = settings.Options.FirstOrDefault(item =>
                item.Configuration == state?.Configuration && item.TargetFramework == state?.TargetFramework)
                ?? settings.Options[0];
            SettingsBox.SelectedItem = options;
            await LoadProject(options, state?.File);
            if (project is not null) SaveProjectState();
        }
        catch (Exception exception) when (!closed) { ShowFailure(exception, "プロジェクトを開けません"); }
        catch (Exception) when (closed) { }
        finally { if (!closed) SetBusy(false); }
    }
}
