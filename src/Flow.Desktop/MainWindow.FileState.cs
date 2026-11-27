using System.IO;
using System.Text.Json;
using Flow.Analysis;
using Flow.Model;

namespace Flow.Desktop;

public partial class MainWindow
{
    private static readonly string FileStatesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "csharp-flow-viewer", "file-states");
    private string? fileStatePath;
    private PreviewViewport? viewport;

    private void ApplyFileState(string statePath, FileViewState? state)
    {
        fileStatePath = statePath;
        if (state is not null)
            foreach (var (key, annotations) in state.Annotations)
                if (annotations is { Marked: not null, GrayRoots: not null })
                    annotationHistory[key] = annotations;
    }

    private string GetFileStatePath(ProjectFile file)
    {
        var options = project!.Options;
        return Path.Combine(FileStatesDirectory,
            FileViewState.Key(Path.GetFullPath(path!).ToUpperInvariant(), Path.GetFullPath(file.Path).ToUpperInvariant(),
                options.Configuration, options.TargetFramework) + ".json");
    }

    private static FileViewState? ReadFileState(string statePath)
    {
        try
        {
            return FileViewState.Read(statePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void SaveFileState()
    {
        if (fileStatePath is null || !HasSource) return;
        try { CaptureFileState().Write(fileStatePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus("ファイルの表示状態を保存できません。", attention: true);
        }
    }

    private FileViewState CaptureFileState()
    {
        var caret = Editor.TextArea.Caret;
        return new FileViewState(1, method is { } entry ? FileViewState.Key(entry.Signature) : null,
            method?.Source.Line ?? 0, caret.Line, caret.Column, Editor.VerticalOffset,
            AnnotationKey, selectedNode?.Id, viewport, annotationHistory)
        {
            SourceFolds = SavedSourceFolds()
        };
    }

    private static MethodEntry? FindSavedMethod(IReadOnlyList<MethodEntry> methods, FileViewState state)
    {
        if (state.Method is null) return null;
        var entries = methods.Where(entry => FileViewState.Key(entry.Signature) == state.Method).ToArray();
        return entries.Length == 1 ? entries[0] : entries.FirstOrDefault(entry => entry.Source.Line == state.MethodLine);
    }

    private void RestoreFileState(FileViewState state)
    {
        if (state.SourceLine > 0)
        {
            var line = Editor.Document.GetLineByNumber(Math.Min(state.SourceLine, Editor.LineCount));
            var offset = line.Offset + Math.Clamp(state.SourceColumn, 1, line.Length + 1) - 1;
            NavigateSource(new SourceRange(offset, 0, line.LineNumber));
            Editor.UpdateLayout();
            if (double.IsFinite(state.SourceScroll) && state.SourceScroll >= 0)
                Editor.ScrollToVerticalOffset(state.SourceScroll);
        }

        if (AnnotationKey is not { } key || state.Diagram != key) return;
        viewport = state.Viewport is { IsValid: true } saved ? saved : null;
        if (state.Node is { } id && nodes.TryGetValue(id, out var node))
            SelectNode(node, navigateSource: false);
    }
}
