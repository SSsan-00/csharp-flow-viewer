using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Flow.Desktop;

internal sealed class PreviewHost(WebView2CompositionControl web)
{
    private const string Origin = "https://csharp-flow-viewer.invalid/";
    private const string Home = Origin + "index.html";
    private readonly Dictionary<string, (byte[] Content, string Mime)> assets = new();
    private readonly string profilePath = Path.Combine(Path.GetTempPath(), "csharp-flow-viewer-" + Guid.NewGuid().ToString("N"));
    public event Action<string, JsonElement, int>? Message;

    public async Task InitializeAsync(CancellationToken token)
    {
        var assembly = typeof(PreviewHost).Assembly;
        foreach (var (file, mime) in new[] { ("index.html", "text/html"), ("preview.css", "text/css"),
                     ("preview.js", "text/javascript"), ("large-diagram.js", "text/javascript"),
                     ("mermaid.bundle.js", "text/javascript") })
        {
            var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(".Preview." + file, StringComparison.Ordinal));
            using var resource = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            resource.CopyTo(copy);
            assets.Add(Origin + file, (copy.ToArray(), mime));
        }

        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath,
            options: new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-breakpad"));
        token.ThrowIfCancellationRequested();
        var controller = environment.CreateCoreWebView2ControllerOptions();
        controller.IsInPrivateModeEnabled = true;
        await web.EnsureCoreWebView2Async(environment, controller).WaitAsync(TimeSpan.FromSeconds(15), token);
        token.ThrowIfCancellationRequested();
        var core = web.CoreWebView2;
        var pageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.NavigationStarting += (_, args) => args.Cancel = args.Uri != Home;
        core.FrameNavigationStarting += (_, args) => args.Cancel = true;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += (_, args) =>
        {
            // WebView2 recovers from failures in auxiliary processes such as the GPU.
            if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessExited)
                Message?.Invoke("process-error", default, 0);
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
                pageReady.TrySetException(new InvalidOperationException($"WebView2 navigation failed ({args.WebErrorStatus})."));
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (args.Request.Method == "GET" && assets.TryGetValue(args.Request.Uri, out var asset))
                args.Response = environment.CreateWebResourceResponse(new MemoryStream(asset.Content, writable: false), 200, "OK",
                    "Content-Type: " + asset.Mime + "; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            else
                args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        };
        core.WebMessageReceived += (_, args) =>
        {
            if (args.Source != Home)
                return;
            using var payload = JsonDocument.Parse(args.WebMessageAsJson);
            var root = payload.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() is { } name &&
                root.TryGetProperty("value", out var value) && root.TryGetProperty("version", out var version))
            {
                if (name == "ready")
                    pageReady.TrySetResult();
                else if (name == "startup-error")
                    pageReady.TrySetException(new InvalidOperationException("The Mermaid renderer did not load."));
                else
                    Message?.Invoke(name, value, version.GetInt32());
            }
        };
        core.Navigate(Home);
        await pageReady.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
    }

    public void Send<T>(T message) => web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    public void Close()
    {
        web.Dispose();
        try { Directory.Delete(profilePath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
