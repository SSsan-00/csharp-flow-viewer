using System.Security.Cryptography;
using System.Text.Json;

namespace Flow.Model;

public sealed record PreviewViewport(double X, double Y, double Scale)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && Scale is >= .000001 and <= 3;
}

public sealed record FileViewState(
    int Version, string? Method, int MethodLine, int SourceLine, int SourceColumn, double SourceScroll,
    string? Diagram, string? Node, PreviewViewport? Viewport, Dictionary<string, NodeAnnotations> Annotations)
{
    public FoldViewState? SourceFolds { get; init; }

    public static string Key(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(parts)));

    public static FileViewState? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<FileViewState>(stream);
            return state is { Version: 1, Annotations: not null } ? state : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, this);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

public sealed record FoldViewState(string ContentHash, int[] Starts);
