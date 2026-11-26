using Flow.Model;

namespace Flow.Analysis;

/// <summary>Maps project-local browser URLs to evaluated files without fetching or following links.</summary>
internal sealed class LocalJavaScriptFiles
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const long MaxTotalBytes = 8L * 1024 * 1024;
    private const int MaxFileCount = 64;
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string root;
    private readonly string webRoot;
    private readonly IReadOnlySet<string> files;
    private readonly Dictionary<string, string?> cache = new(PathComparer);
    private long bytesRead;

    public LocalJavaScriptFiles(string projectPath, IReadOnlySet<string> files)
    {
        root = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        webRoot = Path.Combine(root, "wwwroot");
        this.files = files;
    }

    public string? ResolveTag(string url, string pagePath, string? route, bool hasBaseHref)
    {
        if (url.StartsWith("~/", StringComparison.Ordinal))
            return Resolve(url[2..], webRoot);
        if (hasBaseHref) return null;
        if (url.StartsWith("/", StringComparison.Ordinal))
            return url.StartsWith("//", StringComparison.Ordinal) ? null : Resolve(url[1..], webRoot);
        // A relative URL is relative to the page URL, not the Pages physical directory.
        // Only use the conventional Pages route when the page supplies no custom template.
        if (!string.IsNullOrEmpty(route)) return null;
        var pagesRoot = Path.Combine(root, "Pages");
        var pageDirectory = Path.GetDirectoryName(pagePath)!;
        var relative = Path.GetRelativePath(pagesRoot, pageDirectory);
        return relative.IsWithinDirectory() ? Resolve(url, Path.Combine(webRoot, relative)) : null;
    }

    public string? ResolveImport(string specifier, string sourcePath, string pagePath, string? route, bool hasBaseHref)
    {
        if (PathComparer.Equals(sourcePath, pagePath))
        {
            if (!specifier.StartsWith("/", StringComparison.Ordinal) &&
                !specifier.StartsWith("./", StringComparison.Ordinal) &&
                !specifier.StartsWith("../", StringComparison.Ordinal)) return null;
            return ResolveTag(specifier, pagePath, route, hasBaseHref);
        }
        if (specifier.StartsWith("/", StringComparison.Ordinal))
            return specifier.StartsWith("//", StringComparison.Ordinal) ? null : Resolve(specifier[1..], webRoot);
        if (!specifier.StartsWith("./", StringComparison.Ordinal) &&
            !specifier.StartsWith("../", StringComparison.Ordinal)) return null;
        return Resolve(specifier, Path.GetDirectoryName(sourcePath)!);
    }

    private string? Resolve(string url, string directory)
    {
        var delimiter = url.IndexOfAny(['?', '#']);
        var relative = delimiter < 0 ? url : url[..delimiter];
        if (relative.Length == 0 || relative.Contains('%')) return null; // No URL rewriting, including encoded separators.
        if (relative.IndexOfAny([':', '\\']) >= 0 || relative.Any(char.IsControl) || Path.IsPathRooted(relative)) return null;
        try
        {
            var path = Path.GetFullPath(relative, Path.GetFullPath(directory));
            return files.Contains(path) && Path.GetRelativePath(webRoot, path).IsWithinDirectory() ? path : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    public RazorJavaScriptFragment? Read(string path, bool module, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!files.Contains(path) || !Path.GetRelativePath(root, path).IsWithinDirectory()) return null;
        if (!cache.TryGetValue(path, out var text))
        {
            if (cache.Count >= MaxFileCount) return null;
            text = ReadLocal(path, token);
            cache[path] = text;
        }
        return text is null ? null : new RazorJavaScriptFragment(text, new SourceRange(0, text.Length, 1, path), IsModule: module);
    }

    private string? ReadLocal(string path, CancellationToken token)
    {
        try
        {
            if (OperatingSystem.IsWindows() && root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            // A lexical containment check alone cannot protect against junctions/symlinks.
            for (var current = path; ;)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
                if (PathComparer.Equals(current, root)) break;
                var parent = Path.GetDirectoryName(current);
                if (parent is null) return null;
                current = parent;
            }
            using var input = File.OpenRead(path);
            var length = input.Length;
            if (length > MaxFileBytes || length > MaxTotalBytes - bytesRead) return null;
            var buffer = new byte[(int)length];
            input.ReadExactly(buffer);
            if (input.ReadByte() >= 0) return null; // Do not read an unexpectedly growing file.
            token.ThrowIfCancellationRequested();
            bytesRead += length;
            using var reader = new StreamReader(new MemoryStream(buffer));
            return reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

internal sealed record RazorScriptReference(string Url, SourceRange Source, bool IsModule, bool IsDynamic, bool HasDeferredExecution, bool IsOrderedDeferred);
