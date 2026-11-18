using System.Text.RegularExpressions;

namespace Flow.Analysis;

public sealed class CallExclusions
{
    private readonly Regex[] patterns;
    public IReadOnlyList<string> Patterns { get; }

    public CallExclusions(IEnumerable<string> entries)
    {
        Patterns = entries.Select(entry => entry.Trim()).Where(entry => entry.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        patterns = Patterns.Select(pattern => new Regex(@"\A(?:" + pattern + @")\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))).ToArray();
    }

    internal bool Matches(string method, string type, string qualifiedType) =>
        patterns.Any(pattern => pattern.IsMatch(method) || pattern.IsMatch(type + "." + method)
            || pattern.IsMatch(qualifiedType + "." + method));
}
