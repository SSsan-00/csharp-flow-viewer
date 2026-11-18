namespace Flow.Model;

public sealed record SourceRange(int Offset, int Length, int Line);

public enum NodeKind { Start, End, Call, Condition, Transfer, Exception, Junction }

public sealed record FlowNode(string Id, NodeKind Kind, string Label, SourceRange? Source,
    int? TargetMethod = null, bool IsReachable = true);

public sealed record FlowEdge(string From, string To, string? Label = null, bool IsReachable = true);

public sealed record FlowDiagram(IReadOnlyList<FlowNode> Nodes, IReadOnlyList<FlowEdge> Edges,
    IReadOnlyList<string> Notices, bool IsPartial = false);
