using System.Globalization;
using System.Text;
using Flow.Model;

namespace Flow.Mermaid;

public static class MermaidWriter
{
    public static string Write(FlowDiagram diagram, bool light = false)
    {
        var text = new StringBuilder("flowchart TD\n");
        foreach (var node in diagram.Nodes)
        {
            var (open, close) = node.Kind switch
            {
                NodeKind.Start or NodeKind.End => ("([", "])"),
                NodeKind.Condition => ("{", "}"),
                _ => ("[", "]")
            };
            var label = node.Label + (node.Source is { } source ? $"  ·  L{source.Line}" : "");
            text.Append("  ").Append(node.Id).Append(open).Append('"').Append(Escape(label))
                .Append('"').Append(close).Append(":::flow_").Append(node.IsReachable
                    ? node.Kind.ToString().ToLowerInvariant() : "unreachable").Append('\n');
        }
        foreach (var edge in diagram.Edges)
        {
            text.Append("  ").Append(edge.From).Append(edge.IsReachable ? " -->" : " -.->");
            if (edge.Label is { } label)
                text.Append("|\"").Append(Escape(label)).Append("\"|");
            text.Append(' ').Append(edge.To).Append('\n');
        }
        text.Append(light ? """
              classDef flow_start,flow_end fill:#d9f1e8,stroke:#26745e,color:#1b3d33,stroke-width:2px
              classDef flow_condition fill:#eae4ff,stroke:#6f56bc,color:#32265e,stroke-width:2px
              classDef flow_call fill:#dceeea,stroke:#3b8076,color:#203e3b
              classDef flow_transfer fill:#ece7fa,stroke:#7766a7,color:#35294f
              classDef flow_exception fill:#f9e4ea,stroke:#aa516c,color:#522a3a
              classDef flow_junction fill:#e7ebf7,stroke:#63729c,color:#2a3756
              classDef flow_unreachable fill:#e7e9ef,stroke:#697386,color:#333c50,stroke-dasharray:4 3
            """ : """
              classDef flow_start,flow_end fill:#17453c,stroke:#75e4b6,color:#f0fff9,stroke-width:2px
              classDef flow_condition fill:#382f62,stroke:#aaa0fa,color:#fbf9ff,stroke-width:2px
              classDef flow_call fill:#183d3d,stroke:#69c9ae,color:#eafff8
              classDef flow_transfer fill:#302b4b,stroke:#b2a5ee,color:#faf6ff
              classDef flow_exception fill:#533243,stroke:#f1a2b0,color:#fff3f5
              classDef flow_junction fill:#29344d,stroke:#92a3c6,color:#f2f6ff
              classDef flow_unreachable fill:#343843,stroke:#a0a7b5,color:#f5f6f9,stroke-dasharray:4 3
            """);
        return text.Append('\n').ToString();
    }

    // Mermaid's decimal entity syntax is resolved after parsing. Encode parser
    // punctuation, including the entity introducer, and keep Unicode readable.
    private static string Escape(string value)
    {
        var text = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
                text.Append(' ');
            else if (rune.IsAscii && !Rune.IsLetterOrDigit(rune))
                text.Append('#').Append(rune.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
            else
                text.Append(rune);
        }
        return text.ToString();
    }
}
