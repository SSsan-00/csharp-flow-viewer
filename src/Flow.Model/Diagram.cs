namespace Flow.Model;

// FilePath is optional so existing single-file C# diagrams and saved annotations remain compatible.
// Razor entries carry their original document, rather than an offset into generated C# or JavaScript.
public sealed record SourceRange(int Offset, int Length, int Line, string? FilePath = null);

public enum NodeKind { Start, End, Call, Condition, Transfer, Exception, Junction }

public sealed record ImplementationCandidate(string Label, SourceRange Declaration, int TargetMethod, string? TargetFilePath = null);

public sealed record FlowTarget(string Label, int Method, string FilePath);

public sealed record FlowNode(string Id, NodeKind Kind, string Label, SourceRange? Source,
    int? TargetMethod = null, bool IsReachable = true, bool IsCaseTest = false,
    string? TargetFilePath = null, bool RequiresRuntimeDispatch = false, SourceRange? TargetDeclaration = null,
    IReadOnlyList<ImplementationCandidate>? ImplementationCandidates = null,
    IReadOnlyList<FlowTarget>? CallTargets = null);

public sealed record FlowEdge(string From, string To, string? Label = null, bool IsReachable = true);

public sealed record FlowDiagram(IReadOnlyList<FlowNode> Nodes, IReadOnlyList<FlowEdge> Edges,
    IReadOnlyList<string> Notices, bool IsPartial = false);
