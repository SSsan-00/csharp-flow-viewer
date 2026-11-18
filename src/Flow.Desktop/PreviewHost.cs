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

    public async Task InitializeAsync()
    {
        var assembly = typeof(PreviewHost).Assembly;
        foreach (var (file, mime) in new[] { ("index.html", "text/html"), ("preview.css", "text/css"),
                     ("preview.js", "text/javascript"), ("mermaid.bundle.js", "text/javascript") })
        {
            var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(".Preview." + file, StringComparison.Ordinal));
            using var resource = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            resource.CopyTo(copy);
            assets.Add(Origin + file, (copy.ToArray(), mime));
        }

        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath,
            options: new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-breakpad"));
        var controller = environment.CreateCoreWebView2ControllerOptions();
        controller.IsInPrivateModeEnabled = true;
        await web.EnsureCoreWebView2Async(environment, controller);
        var core = web.CoreWebView2;
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
        core.ProcessFailed += (_, _) => Message?.Invoke("process-error", default, 0);
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) Message?.Invoke("process-error", default, 0);
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
                Message?.Invoke(name, value, version.GetInt32());
        };
        core.Navigate(Home);
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
