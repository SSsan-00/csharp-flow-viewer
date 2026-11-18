using System.IO;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Flow.Desktop;

internal static class Theme
{
    private static readonly string Settings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "csharp-flow-viewer", "theme");
    private static readonly string[] Names =
        ["Window", "Panel", "Raised", "Canvas", "Border", "Text", "Muted", "Disabled", "Accent", "Selected", "Good", "Warning"];

    public static bool Read()
    {
        try { return File.ReadAllText(Settings) != "light"; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static bool Save(bool dark)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
            File.WriteAllText(Settings, dark ? "dark" : "light");
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static void Apply(Window window, TextEditor editor, bool dark)
    {
        string[] values = dark
            ? ["#101420", "#181E2C", "#202738", "#111622", "#353D52", "#EFF0FA", "#B0B9D1",
                "#8490AA", "#C2B6FF", "#40346E", "#78DDB4", "#FFC782"]
            : ["#EEF1F9", "#FBFCFF", "#EDF0F8", "#F7F8FC", "#C8CFE0", "#222B43", "#515F7D",
                "#66728C", "#5540AC", "#DFD8FF", "#146B50", "#843E04"];
        for (var i = 0; i < Names.Length; i++)
            window.Resources[Names[i] + "Brush"] = Brush(values[i]);

        editor.TextArea.SelectionBrush = Brush(dark ? "#564592" : "#D9CFFF");
        editor.TextArea.SelectionForeground = Brush(dark ? "#FFFFFF" : "#151B27");
        editor.SyntaxHighlighting = null;
        var highlighting = HighlightingManager.Instance.GetDefinition("C#");
        foreach (var color in highlighting.NamedHighlightingColors)
        {
            var (night, day) = color.Name switch
            {
                "Comment" or "Preprocessor" => ("#A1B2A9", "#386449"),
                "String" or "Char" => ("#A0E5C4", "#14694A"),
                "MethodCall" => ("#F1D89A", "#76540B"),
                "NumberLiteral" or "TrueFalse" => ("#91DBED", "#1A6378"),
                "Keywords" or "GotoKeywords" or "ContextKeywords" or "ExceptionKeywords" or "GetSetAddRemove"
                    => ("#CBB0FA", "#6939A7"),
                "StringInterpolation" or "Punctuation" => (values[5], values[5]),
                _ => ("#8CCBFA", "#205B9C")
            };
            color.Foreground = new SimpleHighlightingBrush(Brush(dark ? night : day).Color);
        }
        editor.SyntaxHighlighting = highlighting;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
