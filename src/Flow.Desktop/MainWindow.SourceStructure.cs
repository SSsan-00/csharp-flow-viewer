using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using Flow.Model;
using ICSharpCode.AvalonEdit.Folding;

namespace Flow.Desktop;

public partial class MainWindow
{
    private FoldingManager? sourceFolding;
    private IReadOnlyList<SourceBracket> sourceBrackets = [];
    private string? sourceFoldHash;

    private void InstallSourceFolding()
    {
        sourceFolding = FoldingManager.Install(Editor.TextArea);
        Editor.SetResourceReference(FoldingMargin.FoldingMarkerBrushProperty, "MutedBrush");
        Editor.SetResourceReference(FoldingMargin.FoldingMarkerBackgroundBrushProperty, "CanvasBrush");
        Editor.SetResourceReference(FoldingMargin.SelectedFoldingMarkerBrushProperty, "AccentBrush");
        Editor.SetResourceReference(FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, "CanvasBrush");
    }

    private void SetSourceBrackets(IReadOnlyList<SourceBracket> brackets, FoldViewState? saved)
    {
        sourceBrackets = brackets;
        sourceFoldHash = null;
        if (sourceFolding is not { } folding) return;
        var document = Editor.Document;
        var sections = brackets.Where(pair => pair.CanFold && pair.Open >= 0 && pair.Close < document.TextLength
                && pair.Close > pair.Open + 1 && document.GetLineByOffset(pair.Open).LineNumber
                    != document.GetLineByOffset(pair.Close).LineNumber)
            .DistinctBy(pair => pair.Open).OrderBy(pair => pair.Open)
            .Select(pair => new NewFolding(pair.Open + 1, pair.Close) { Name = "…" });
        folding.UpdateFoldings(sections, -1);
        if (saved is not { Starts.Length: > 0 } || saved.ContentHash != SourceFoldHash()) return;
        var starts = saved.Starts.ToHashSet();
        foreach (var section in folding.AllFoldings)
            section.IsFolded = starts.Contains(section.StartOffset);
    }

    private FoldViewState? SavedSourceFolds()
    {
        var starts = sourceFolding?.AllFoldings.Where(section => section.IsFolded)
            .Select(section => section.StartOffset).ToArray();
        return starts is { Length: > 0 } ? new FoldViewState(SourceFoldHash(), starts) : null;
    }

    private string SourceFoldHash() => sourceFoldHash ??=
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(Editor.Text.AsSpan())));

    private void RevealSourceOffset(int offset)
    {
        if (sourceFolding is not { } folding) return;
        foreach (var section in folding.GetFoldingsContaining(offset))
            if (section.IsFolded) section.IsFolded = false;
    }

    private void JumpBracket(object sender, RoutedEventArgs args) => JumpToMatchingBracket();

    private void JumpToMatchingBracket()
    {
        if (busy || !HasSource) return;
        Editor.Focus();
        if (SourceBracket.MatchAt(sourceBrackets, Editor.CaretOffset) is not int target)
        {
            SetStatus("カーソルを対応する括弧の上か直後に置いてください。");
            return;
        }
        var line = Editor.Document.GetLineByOffset(target).LineNumber;
        NavigateSource(new SourceRange(target, 1, line));
        Editor.CaretOffset = target;
        SetStatus($"対応する括弧へ移動しました（L{line}）。");
    }
}
