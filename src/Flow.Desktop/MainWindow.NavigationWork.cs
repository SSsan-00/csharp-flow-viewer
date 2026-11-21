using System.Windows;
using System.Diagnostics;
using Flow.Analysis;
using Flow.Mermaid;
using Flow.Model;

namespace Flow.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? operationCancellation;

    private sealed record LoadedSource(SourceFile? CSharp, RazorSourceFile? Razor, JavaScriptSourceFile? JavaScript)
    {
        public object Identity => (object?)CSharp ?? (object?)Razor ?? JavaScript!.NavigationContext;
        public string Text => CSharp?.Text ?? Razor?.Text ?? JavaScript!.Text;
        public IReadOnlyList<MethodEntry> Entries => CSharp?.Methods ?? Razor?.Entries ?? JavaScript!.Entries;
        public IReadOnlyList<string> Notices => CSharp?.Notices ?? Razor?.Notices ?? JavaScript!.Notices;
    }

    private sealed record PreparedFlow(FlowDiagram Graph, string? Mermaid);
    private sealed record PreparedNavigation(LoadedSource Source, MethodEntry? Entry, PreparedFlow? Flow);

    private void CancelOperation(object sender, RoutedEventArgs args)
    {
        if (operationCancellation is not { IsCancellationRequested: false } cancellation) return;
        cancellation.Cancel();
        SetStatus("処理を中断しています…");
        UpdateSurface();
    }

    // Commit changes to the displayed view only after preparation succeeds.
    private async Task<bool> RunOperationAsync<T>(string status, Func<CancellationToken, Task<T>> prepare, Action<T> apply, string failure = "ナビゲーションを完了できませんでした。", string timing = "navigation")
    {
        if (busy || closed) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operationCancellation = cancellation;
        var started = Stopwatch.GetTimestamp();
        SetBusy(true);
        SetStatus(status);
        try
        {
            var result = await prepare(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closed) return false;
            operationCancellation = null;
            apply(result);
            FlowTiming.Log.Record(timing, started, version);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!closed) SetStatus("中断しました。処理前の表示を保持しています。");
            return false;
        }
        catch (Exception exception) when (!closed)
        {
            var message = exception is ProjectLoadException ? exception.Message : failure;
            SetStatus(message, attention: true);
            return false;
        }
        catch (Exception) when (closed) { return false; }
        finally
        {
            operationCancellation = null;
            if (!closed) SetBusy(false);
        }
    }

    private async Task<LoadedSource> ReadNavigationSourceAsync(ProjectFile file, RazorSourceFile? razorContext,
        JavaScriptSourceFile? jsContext, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = project!;
        if (file.IsRazor)
        {
            if (razorContext?.FilePath == file.Path) return new(null, razorContext, null);
            if (currentFile == file && razorSource is not null) return new(null, razorSource, null);
            return new(null, await Task.Run(() => current.ReadRazorAsync(file, token), token), null);
        }
        if (file.IsJavaScript)
        {
            if (jsContext?.FilePath == file.Path) return new(null, null, jsContext);
            if (razorContext is null && jsContext is null && currentFile == file && javaScriptSource is not null)
                return new(null, null, javaScriptSource);
            return new(null, null, await Task.Run(() => current.ReadJavaScriptAsync(file, token, razorContext, jsContext), token));
        }
        if (!csharpSources.TryGetValue(file, out var csharp))
        {
            csharp = await Task.Run(() => current.ReadAsync(file, token), token);
            token.ThrowIfCancellationRequested();
            if (csharpSources.Count >= 8)
            {
                var first = csharpSources.First();
                csharpSources.Remove(first.Key);
                flowCache.RemoveWhere(key => ReferenceEquals(key.Source, first.Value));
            }
            csharpSources[file] = csharp;
        }
        return new(csharp, null, null);
    }

    private async Task<PreparedFlow> PrepareFlowAsync(LoadedSource loaded, int id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = (loaded.Identity, loaded.JavaScript?.FilePath, id);
        if (flowCache.TryGetValue(key, out var cached)) return cached;
        var exclusions = excludedCalls;
        var flow = await Task.Run(() =>
        {
            var graph = loaded.CSharp?.Analyze(id, token, exclusions) ?? loaded.Razor?.Analyze(id, token)
                ?? loaded.JavaScript!.Analyze(id, token);
            var prepared = new PreparedFlow(graph, graph.Nodes.Count == 0 ? null : MermaidWriter.Write(graph, token: token));
            return (Prepared: prepared, Weight: FlowCacheWeight.Estimate(graph, prepared.Mermaid));
        }, token);
        token.ThrowIfCancellationRequested();
        flowCache.Add(key, flow.Prepared, flow.Weight);
        return flow.Prepared;
    }

    private async Task<PreparedNavigation> PrepareNavigationAsync(ProjectFile file, int? entryId,
        RazorSourceFile? razorContext, JavaScriptSourceFile? jsContext, CancellationToken token)
    {
        var loaded = await ReadNavigationSourceAsync(file, razorContext, jsContext, token);
        token.ThrowIfCancellationRequested();
        if (entryId is not int id) return new(loaded, null, null);
        var entry = loaded.Entries.FirstOrDefault(item => item.Id == id)
            ?? throw new ProjectLoadException("移動先の解析対象が見つかりません。プロジェクトを再読み込みしてください。");
        var flow = await PrepareFlowAsync(loaded, id, token);
        return new(loaded, entry, flow);
    }

    private void ApplyPreparedFile(ProjectFile file, LoadedSource loaded)
    {
        if (currentFile == file && HasSource && ReferenceEquals(loaded.Identity, new LoadedSource(source, razorSource, javaScriptSource).Identity))
            return;
        ResetFile(preserveHistory: true);
        if (!FilesBox.Items.Contains(file)) FileFilter.Clear();
        FilesBox.SelectedItem = file;
        FilesBox.ScrollIntoView(file);
        var statePath = GetFileStatePath(file);
        ApplyLoadedFile(file, loaded, statePath, ReadFileState(statePath));
    }

    private void ApplyPreparedEntry(ProjectFile file, PreparedNavigation prepared, string? returnTo = null,
        DefinitionView view = DefinitionView.Direct)
    {
        ApplyPreparedFile(file, prepared.Source);
        if (prepared.Entry is { } entry && prepared.Flow is { } flow)
        {
            if (!MethodsBox.Items.Contains(entry)) Filter.Clear();
            MethodsBox.SelectedItem = entry;
            MethodsBox.ScrollIntoView(entry);
            ApplyFlow(entry, flow, returnTo);
            ShowDefinitionView(view);
        }
        SaveProjectState();
    }
}
