using System.IO;
using System.Text.Json;
using Flow.Analysis;

namespace Flow.Desktop;

internal static class TraceSettings
{
    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "csharp-flow-viewer", "trace-exclusions.json");

    public static CallExclusions Read(string path)
    {
        var names = JsonSerializer.Deserialize<string?[]>(File.ReadAllText(path));
        if (names is null || names.Any(name => name is null))
            throw new JsonException("Expected a JSON array of function names.");
        return new CallExclusions(names.Select(name => name!));
    }

    public static void Write(string path, CallExclusions exclusions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(exclusions.Patterns,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
