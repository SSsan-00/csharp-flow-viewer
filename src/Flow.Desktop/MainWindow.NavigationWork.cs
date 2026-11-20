using System.Windows;
using Flow.Analysis;
using Flow.Mermaid;
using Flow.Model;

namespace Flow.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? navigationCancellation;

    private sealed record LoadedSource(SourceFile? CSharp, RazorSourceFile? Razor, JavaScriptSourceFile? JavaScript)
    {
        public object Identity => (object?)CSharp ?? (object?)Razor ?? JavaScript!.NavigationContext;
        public string Text => CSharp?.Text ?? Razor?.Text ?? JavaScript!.Text;
        public IReadOnlyList<MethodEntry> Entries => CSharp?.Methods ?? Razor?.Entries ?? JavaScript!.Entries;
        public IReadOnlyList<string> Notices => CSharp?.Notices ?? Razor?.Notices ?? JavaScript!.Notices;
    }

    private sealed record PreparedFlow(FlowDiagram Graph, string? Mermaid);
    private sealed record PreparedNavigation(LoadedSource Source, MethodEntry? Entry, PreparedFlow? Flow);

    private void CancelNavigation(object sender, RoutedEventArgs args)
    {
        if (navigationCancellation is not { IsCancellationRequested: false } cancellation) return;
        cancellation.Cancel();
        SetStatus("移動を中断しています…");
        UpdateSurface();
    }

    // No editor, diagram or history change is allowed until preparation has completed.
    private async Task<bool> RunNavigationAsync<T>(string status, Func<CancellationToken, Task<T>> prepare, Action<T> apply)
    {
        if (busy || closed) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        navigationCancellation = cancellation;
        SetBusy(true);
        SetStatus(status);
        try
        {
            var result = await prepare(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closed) return false;
            navigationCancellation = null;
            apply(result);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!closed) SetStatus("中断しました。移動前の表示を保持しています。");
            return false;
        }
        catch (Exception exception) when (!closed)
        {
            var message = exception is ProjectLoadException ? exception.Message : "ナビゲーションを完了できませんでした。";
            SetStatus(message, attention: true);
            return false;
        }
        catch (Exception) when (closed) { return false; }
        finally
        {
            navigationCancellation = null;
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
                foreach (var key in flowCache.Keys.Where(key => ReferenceEquals(key.Source, first.Value)).ToArray())
                    flowCache.Remove(key);
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
            return new PreparedFlow(graph, graph.Nodes.Count == 0 ? null : MermaidWriter.Write(graph, token: token));
        }, token);
        token.ThrowIfCancellationRequested();
        if (flow.Graph.Nodes.Count <= 4_000 && flow.Graph.Edges.Count <= 8_000 && (flow.Mermaid?.Length ?? 0) <= 350_000)
        {
            if (flowCache.Count >= 16) flowCache.Remove(flowCache.Keys.First());
            flowCache[key] = flow;
        }
        return flow;
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
        ApplyLoadedFile(file, loaded);
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
