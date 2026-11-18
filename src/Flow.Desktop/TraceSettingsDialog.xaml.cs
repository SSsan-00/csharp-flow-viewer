using System.IO;
using System.Text.Json;
using System.Windows;
using Flow.Analysis;
using Microsoft.Win32;

namespace Flow.Desktop;

public partial class TraceSettingsDialog : Window
{
    internal TraceSettingsDialog(Window owner, CallExclusions exclusions)
    {
        Owner = owner;
        foreach (var key in owner.Resources.Keys)
            Resources[key] = owner.Resources[key];
        InitializeComponent();
        Display(exclusions);
    }

    internal CallExclusions ExcludedCalls { get; private set; } = new([]);

    private void Display(CallExclusions exclusions) =>
        Names.Text = string.Join(Environment.NewLine, exclusions.Patterns);

    private void Import(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Display(TraceSettings.Read(dialog.FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            ShowFailure("正しい正規表現を文字列の配列で記述した JSON ファイルを選択してください。");
        }
    }

    private void Export(object sender, RoutedEventArgs args)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json", DefaultExt = ".json", FileName = "trace-exclusions.json"
        };
        if (dialog.ShowDialog(this) == true)
            SaveDraft(dialog.FileName);
    }

    private void Save(object sender, RoutedEventArgs args)
    {
        if (SaveDraft(TraceSettings.SettingsPath) is not { } exclusions) return;
        ExcludedCalls = exclusions;
        DialogResult = true;
    }

    private CallExclusions? SaveDraft(string path)
    {
        try
        {
            var exclusions = new CallExclusions(Names.Text.Split('\n'));
            TraceSettings.Write(path, exclusions);
            return exclusions;
        }
        catch (ArgumentException)
        {
            ShowFailure("正規表現の書式を確認してください。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowFailure("追跡設定を保存できません。保存先とアクセス権を確認してください。");
        }
        return null;
    }

    private void ShowFailure(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
