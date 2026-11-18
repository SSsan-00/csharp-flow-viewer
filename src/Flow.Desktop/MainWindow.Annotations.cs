using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using Flow.Model;
using Microsoft.Win32;

namespace Flow.Desktop;

public partial class MainWindow
{
    private sealed record AnnotationFile(int Version, string Diagram, NodeAnnotations Annotations);
    private readonly Dictionary<string, NodeAnnotations> annotationHistory = new();
    private readonly SourceAnnotationColorizer editorAnnotations = new();

    private string? AnnotationKey { get; set; }

    private void UpdateAnnotationKey() => AnnotationKey = method is { } entry && mermaid is { } graph
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{SourceTitle.Text}\n{entry.Signature}\n{entry.Source.Offset}\n{graph}")))
        : null;

    private void OpenAnnotationsMenu(object sender, RoutedEventArgs args)
    {
        var menu = AnnotationsButton.ContextMenu;
        menu.PlacementTarget = AnnotationsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ChangeAnnotation(JsonElement value)
    {
        if (AnnotationKey is not { } key || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("id", out var idValue) || idValue.GetString() is not { } id || !nodes.ContainsKey(id)
            || !value.TryGetProperty("action", out var actionValue))
            return;

        if (!annotationHistory.TryGetValue(key, out var annotations))
            annotationHistory[key] = annotations = new NodeAnnotations();

        switch (actionValue.GetString())
        {
            case "mark":
                annotations.GrayRoots.Remove(id);
                annotations.Marked.Add(id);
                break;
            case "gray":
                annotations.Marked.Remove(id);
                annotations.GrayRoots.Add(id);
                break;
            case "clear":
                annotations.Marked.Remove(id);
                annotations.GrayRoots.Remove(id);
                break;
        }
        ShowAnnotations();
        SaveFileState();
    }

    private void ShowAnnotations()
    {
        if (diagram is not { } graph || AnnotationKey is not { } key) return;
        var annotations = annotationHistory.GetValueOrDefault(key) ?? new NodeAnnotations();
        var gray = annotations.GrayNodes(graph);
        editorAnnotations.SetRanges(Editor.Document, graph.Nodes, annotations.Marked, gray);
        Editor.TextArea.TextView.Redraw();
        if (rendered)
            preview.Send(new { type = "annotations", marked = annotations.Marked, gray });
    }

    private void ExportAnnotations(object sender, RoutedEventArgs args)
    {
        if (AnnotationKey is not { } key) return;
        var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json", DefaultExt = ".json", FileName = "flow-annotations.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var annotations = annotationHistory.GetValueOrDefault(key) ?? new NodeAnnotations();
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new AnnotationFile(1, key, annotations),
                new JsonSerializerOptions { WriteIndented = true }));
            SetStatus("注釈を保存しました。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus("注釈を保存できません。保存先とアクセス権を確認してください。", attention: true);
        }
    }

    private void ImportAnnotations(object sender, RoutedEventArgs args)
    {
        if (AnnotationKey is not { } key) return;
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var file = JsonSerializer.Deserialize<AnnotationFile>(File.ReadAllText(dialog.FileName));
            if (file is not { Version: 1, Annotations: { Marked: { } marked, GrayRoots: { } roots } }
                || marked.Any(id => string.IsNullOrEmpty(id)) || roots.Any(id => string.IsNullOrEmpty(id)))
            {
                SetStatus("注釈の JSON 形式を確認してください。", attention: true);
                return;
            }
            if (file.Diagram != key || !marked.Concat(roots).All(nodes.ContainsKey))
            {
                SetStatus("この注釈ファイルに対応するメソッドを開いてください。", attention: true);
                return;
            }
            annotationHistory[key] = file.Annotations;
            ShowAnnotations();
            SetStatus("注釈を読み込みました。");
            SaveFileState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus("注釈の JSON を読み込めません。ファイルを確認してください。", attention: true);
        }
    }
}
