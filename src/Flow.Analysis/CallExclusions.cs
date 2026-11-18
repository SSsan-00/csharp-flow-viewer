using System.Text.RegularExpressions;

namespace Flow.Analysis;

public sealed class CallExclusions
{
    private readonly HashSet<string> exactNames = new(StringComparer.Ordinal);
    private readonly List<Regex> regexPatterns = [];
    public IReadOnlyList<string> Patterns { get; }

    public CallExclusions(IEnumerable<string> entries)
    {
        Patterns = entries.Select(entry => entry.Trim()).Where(entry => entry.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var pattern in Patterns)
        {
            // Dots alone are name separators. Other regex metacharacters opt into regex matching.
            if (pattern.AsSpan().IndexOfAny(@"\^$|?*+()[]{}") < 0)
                exactNames.Add(pattern);
            else
                regexPatterns.Add(new Regex(@"\A(?:" + pattern + @")\z",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
        }
    }

    internal bool Matches(string method, string type, string qualifiedType)
    {
        var typeMethod = type + "." + method;
        var qualifiedMethod = qualifiedType + "." + method;
        return exactNames.Contains(method) || exactNames.Contains(typeMethod) || exactNames.Contains(qualifiedMethod)
            || regexPatterns.Any(pattern => pattern.IsMatch(method) || pattern.IsMatch(typeMethod)
                || pattern.IsMatch(qualifiedMethod));
    }
}
