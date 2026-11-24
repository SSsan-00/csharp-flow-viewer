using System.Net;

namespace Flow.Analysis;

/// <summary>
/// Reads elements from Razor's markup-only projection. This small HTML reader never
/// sees C# strings, Razor comments, or script contents as HTML elements.
/// </summary>
internal sealed class RazorMarkupReader
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"
    };
    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "textarea", "title" };

    public RazorMarkupReader(RazorMarkupDocument document, string original, CancellationToken token)
    {
        var html = document.Html;
        var elements = new List<RazorElement>();
        var stack = new List<RazorElement>();
        var scripts = new List<RazorJavaScriptFragment>();
        var references = new List<RazorScriptReference>();
        var notices = new List<string>();
        var position = 0;
        while (position < html.Length)
        {
            token.ThrowIfCancellationRequested();
            if (html[position] != '<') { position++; continue; }
            if (html.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = html.IndexOf("-->", position + 4, StringComparison.Ordinal);
                position = commentEnd < 0 ? html.Length : commentEnd + 3;
                continue;
            }
            var start = position++;
            var closing = position < html.Length && html[position] == '/';
            if (closing) position++;
            if (position >= html.Length || !char.IsLetter(html[position])) continue;
            var nameStart = position;
            while (position < html.Length && IsName(html[position])) position++;
            var tag = html[nameStart..position].ToLowerInvariant();
            var attributes = new Dictionary<string, RazorAttribute>(StringComparer.OrdinalIgnoreCase);
            var selfClosing = false;
            while (position < html.Length)
            {
                SkipWhite();
                if (position >= html.Length) break;
                if (html[position] == '>') { position++; break; }
                if (html[position] == '/' && position + 1 < html.Length && html[position + 1] == '>')
                { position += 2; selfClosing = true; break; }
                var attributeStart = position;
                while (position < html.Length && !char.IsWhiteSpace(html[position]) && html[position] is not '=' and not '>' and not '/') position++;
                if (position == attributeStart) { position++; continue; }
                var attributeName = html[attributeStart..position].ToLowerInvariant();
                SkipWhite();
                var valueStart = position;
                var valueEnd = position;
                if (position < html.Length && html[position] == '=')
                {
                    position++;
                    SkipWhite();
                    var quote = position < html.Length && html[position] is '\'' or '"' ? html[position++] : '\0';
                    valueStart = position;
                    while (position < html.Length && (quote == '\0'
                               ? !char.IsWhiteSpace(html[position]) && html[position] != '>'
                               : html[position] != quote)) position++;
                    valueEnd = position;
                    if (quote != '\0' && position < html.Length) position++;
                }
                var rawValue = original[valueStart..valueEnd];
                // HTML keeps the first duplicate attribute. Do the same so a second
                // handler/action attribute cannot silently override the displayed one.
                attributes.TryAdd(attributeName, new RazorAttribute(attributeName,
                    WebUtility.HtmlDecode(rawValue), rawValue, attributeStart, position - attributeStart,
                    valueStart, valueEnd - valueStart, document.IsDynamic(valueStart, valueEnd - valueStart)));
            }
            if (closing)
            {
                var match = stack.FindLastIndex(element => element.TagName.Equals(tag, StringComparison.OrdinalIgnoreCase));
                if (match >= 0)
                {
                    stack[match].End = position;
                    stack[match].ContentEnd = start;
                    stack.RemoveRange(match, stack.Count - match);
                }
                continue;
            }
            var element = new RazorElement(tag, start, position, attributes, stack.LastOrDefault());
            elements.Add(element);
            if (RawTextElements.Contains(tag))
            {
                var contentEnd = FindRawTextEnd(html, tag, position);
                element.ContentEnd = contentEnd;
                element.End = contentEnd;
                if (tag == "script")
                {
                    if (IsJavaScript(element))
                    {
                        var module = element.Attribute("type")?.Value.Trim().Equals("module", StringComparison.OrdinalIgnoreCase) == true;
                        if (element.Attribute("src") is { } scriptSource)
                            references.Add(new RazorScriptReference(scriptSource.Value,
                                document.Range(scriptSource.ValueStart, scriptSource.ValueLength), module, HasDynamicScriptPath(document, scriptSource),
                                element.Attributes.Keys.Any(name => name is "async" or "defer" or "nomodule"),
                                element.Attribute("defer") is { IsDynamic: false }
                                    && element.Attribute("async") is null && element.Attribute("nomodule") is null));
                        else scripts.Add(document.ProjectScript(position, contentEnd - position, module));
                    }
                    else if (element.Attribute("language") is { } language && !language.Value.Equals("javascript", StringComparison.OrdinalIgnoreCase))
                        notices.Add($"L{document.Range(start, 1).Line}: {language.Value} のスクリプトはJavaScriptとして解析しません。");
                }
                position = contentEnd;
            }
            else if (!selfClosing && !VoidElements.Contains(tag)) stack.Add(element);
        }
        Elements = elements;
        Scripts = scripts;
        References = references;
        Notices = notices;

        void SkipWhite() { while (position < html.Length && char.IsWhiteSpace(html[position])) position++; }
    }

    public IReadOnlyList<RazorElement> Elements { get; }
    public IReadOnlyList<RazorJavaScriptFragment> Scripts { get; }
    public IReadOnlyList<RazorScriptReference> References { get; }
    public IReadOnlyList<string> Notices { get; }

    private static bool HasDynamicScriptPath(RazorMarkupDocument document, RazorAttribute source)
    {
        if (!source.IsDynamic) return false;
        var delimiter = source.RawValue.IndexOfAny(['?', '#']);
        if (delimiter < 0 || document.IsDynamic(source.ValueStart, delimiter + 1)) return true;
        var prefix = WebUtility.HtmlDecode(source.RawValue[..(delimiter + 1)]);
        return !source.Value.StartsWith(prefix, StringComparison.Ordinal)
            || source.Value.IndexOfAny(['?', '#']) != prefix.Length - 1;
    }

    private static bool IsName(char value) => char.IsLetterOrDigit(value) || value is '-' or '_' or ':' or '.';

    private static int FindRawTextEnd(string html, string tag, int position)
    {
        while ((position = html.IndexOf("</" + tag, position, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = position + tag.Length + 2;
            if (end == html.Length || char.IsWhiteSpace(html[end]) || html[end] == '>') return position;
            position = end;
        }
        return html.Length;
    }

    private static bool IsJavaScript(RazorElement element)
    {
        if (element.Attribute("type") is { } type && !string.IsNullOrWhiteSpace(type.Value))
            return type.Value.Trim().ToLowerInvariant() is "text/javascript" or "application/javascript"
                or "text/ecmascript" or "application/ecmascript" or "module";
        return element.Attribute("language") is not { } language || string.IsNullOrWhiteSpace(language.Value)
            || language.Value.StartsWith("javascript", StringComparison.OrdinalIgnoreCase)
            || language.Value.Equals("jscript", StringComparison.OrdinalIgnoreCase)
            || language.Value.Equals("ecmascript", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class RazorElement(string tagName, int start, int openEnd,
    IReadOnlyDictionary<string, RazorAttribute> attributes, RazorElement? parent)
{
    public string TagName { get; } = tagName;
    public int Start { get; } = start;
    public int OpenEnd { get; } = openEnd;
    public int ContentEnd { get; set; } = openEnd;
    public int End { get; set; } = openEnd;
    public RazorElement? Parent { get; } = parent;
    public IReadOnlyDictionary<string, RazorAttribute> Attributes { get; } = attributes;
    public RazorAttribute? Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value : null;
}

internal sealed record RazorAttribute(string Name, string Value, string RawValue, int Start, int Length,
    int ValueStart, int ValueLength, bool IsDynamic);
