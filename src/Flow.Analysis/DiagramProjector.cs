using System.Runtime.CompilerServices;
using System.Text;
using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Flow.Analysis;

internal sealed class DiagramProjector(ControlFlowGraph graph, TransferMarkers markers,
    Func<TextSpan, SourceRange> range, IReadOnlySet<int> targets, CancellationToken token, CallExclusions? excludedCalls,
    IReadOnlySet<string>? navigableCSharpPaths)
{
    private readonly record struct State(int Block, int Context);
    private sealed record Continuation(ControlFlowRegion Region, string Resume, int Parent);
    private readonly record struct Visible(NodeKind Kind, string Label, SourceRange? Source, int? Target = null, bool IsCaseTest = false,
        string? TargetFilePath = null, bool RequiresRuntimeDispatch = false, SourceRange? TargetDeclaration = null,
        IReadOnlyList<ImplementationCandidate>? ImplementationCandidates = null);
    private sealed record RegionEntry(ControlFlowRegion Region, ControlFlowRegion? EnclosingFinally);

    private readonly Dictionary<State, string> states = new();
    private readonly Queue<State> pending = new();
    private readonly Dictionary<Continuation, int> contextIds = new();
    private readonly List<Continuation?> contexts = [null];
    private readonly List<FlowNode> nodes = [];
    private readonly List<FlowEdge> edges = [];
    private readonly HashSet<string> transparent = [];
    private readonly List<RegionEntry> regionEntries = [];
    private readonly HashSet<ControlFlowRegion> generatedCleanupRegions = [];
    private readonly Dictionary<CaptureId, IOperation> captures = new();
    private readonly HashSet<CaptureId> ambiguousCaptures = [];
    private ILookup<ControlFlowRegion?, BasicBlock> unreachableBlocks = null!;
    private readonly HashSet<CaptureId> lvalues = [];
    private readonly HashSet<CaptureId> compoundLocations = [];
    private readonly List<string> notices = [];
    private bool partial;
    private readonly Dictionary<int, ExpressionSyntax> switchStarts = [];
    private readonly HashSet<int> switchesWithTests = [];

    public FlowDiagram Build(SourceRange declaration, bool isPartial)
    {
        partial = isPartial;
        foreach (var operation in graph.Blocks.SelectMany(block => block.Operations).SelectMany(op => op.DescendantsAndSelf()))
        {
            if (operation is IFlowCaptureOperation capture && !captures.TryAdd(capture.Id, capture.Value))
                ambiguousCaptures.Add(capture.Id);
            if (operation is IAssignmentOperation { Target: IFlowCaptureReferenceOperation target })
            {
                lvalues.Add(target.Id);
                if (operation is ICompoundAssignmentOperation)
                    compoundLocations.Add(target.Id);
            }
        }

        foreach (var block in graph.Blocks)
        {
            var test = SwitchCase(block.BranchValue);
            var owner = test?.Ancestors().FirstOrDefault(node => node is SwitchStatementSyntax or SwitchExpressionSyntax);
            var input = owner switch
            {
                SwitchStatementSyntax statement => statement.Expression,
                SwitchExpressionSyntax expression => expression.GoverningExpression,
                _ => null
            };
            if (input is not null && switchesWithTests.Add(input.SpanStart))
                switchStarts[block.Ordinal] = input;
        }

        foreach (var block in graph.Blocks)
        {
            var region = EnclosingFinally(block.EnclosingRegion);
            if (region is null) continue;
            if (block.Operations.SelectMany(operation => operation.DescendantsAndSelf())
                .OfType<IInvocationOperation>().Any(IsGeneratedCleanup))
                generatedCleanupRegions.Add(region);
        }

        CollectRegions(graph.Root, null);
        unreachableBlocks = graph.Blocks.Where(block => !block.IsReachable)
            .ToLookup(block => EnclosingFinally(block.EnclosingRegion));
        var start = Add(new Visible(NodeKind.Start, "Start", declaration with { Length = 0 }), true);
        edges.Add(new FlowEdge(start, GetBlock(new State(0, 0))));
        AddRegionEntries(null, 0);
        while (pending.TryDequeue(out var state))
        {
            token.ThrowIfCancellationRequested();
            ProjectBlock(state);
        }
        if (regionEntries.Count != 0 || nodes.Any(node => node.Kind == NodeKind.Exception))
            notices.Add("例外探索・フィルター・暗黙の例外は実行時に決まります。独立した例外入口は候補の領域を示します。");
        if (nodes.Any(node => !node.IsReachable) || edges.Any(edge => !edge.IsReachable))
            notices.Add("破線は Roslyn または定数条件で到達不能と判定されたノード／遷移です。");
        return Simplify();
    }

    private void CollectRegions(ControlFlowRegion region, ControlFlowRegion? enclosingFinally)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (generatedCleanupRegions.Contains(region)) return;
        switch (region.Kind)
        {
            case ControlFlowRegionKind.Catch when region.EnclosingRegion?.Kind != ControlFlowRegionKind.FilterAndHandler:
            case ControlFlowRegionKind.Filter:
            case ControlFlowRegionKind.Finally:
                regionEntries.Add(new RegionEntry(region, enclosingFinally));
                break;
        }
        foreach (var nested in region.NestedRegions)
            CollectRegions(nested, region.Kind == ControlFlowRegionKind.Finally ? region : enclosingFinally);
    }

    private static ControlFlowRegion? EnclosingFinally(ControlFlowRegion? region)
    {
        for (var candidate = region; candidate is not null; candidate = candidate.EnclosingRegion)
            if (candidate.Kind == ControlFlowRegionKind.Finally)
                return candidate;
        return null;
    }

    private void AddRegionEntries(ControlFlowRegion? enclosingFinally, int context)
    {
        foreach (var block in unreachableBlocks[enclosingFinally])
            if (EnclosingFinally(block.EnclosingRegion) is not { } region || !generatedCleanupRegions.Contains(region))
                GetBlock(new State(block.Ordinal, context));

        foreach (var entry in regionEntries.Where(entry => entry.EnclosingFinally == enclosingFinally))
        {
            var region = entry.Region;
            var block = graph.Blocks[region.FirstBlockOrdinal];
            var label = region.Kind switch
            {
                ControlFlowRegionKind.Filter => "filter · 例外探索から",
                ControlFlowRegionKind.Finally => "finally · 例外の巻き戻しから",
                _ => "catch · " + (region.ExceptionType?.ToDisplayString() ?? "例外")
            };
            var from = Add(new Visible(NodeKind.Exception, label, BlockSource(block)), block.IsReachable);
            var destination = region.Kind == ControlFlowRegionKind.Finally
                ? RunFinally(region, Add(new Visible(NodeKind.Exception, "保留中の例外の巻き戻しを継続", null), block.IsReachable), context)
                : GetBlock(new State(block.Ordinal, context));
            edges.Add(new FlowEdge(from, destination, IsReachable: block.IsReachable));
        }
    }

    private string GetBlock(State state)
    {
        if (states.TryGetValue(state, out var id))
            return id;
        var block = graph.Blocks[state.Block];
        id = "b" + state.Block + "_c" + state.Context;
        states.Add(state, id);
        pending.Enqueue(state);
        nodes.Add(block.Kind == BasicBlockKind.Exit
            ? new FlowNode(id, NodeKind.End, "End", null, IsReachable: block.IsReachable)
            : new FlowNode(id, NodeKind.Junction, "合流", BlockSource(block), IsReachable: block.IsReachable));
        if (block.Kind != BasicBlockKind.Exit)
            transparent.Add(id);
        return id;
    }

    private void ProjectBlock(State state)
    {
        var block = graph.Blocks[state.Block];
        var last = states[state];
        foreach (var operation in block.Operations)
        {
            Append(Evaluate(operation));
            if (operation is IFlowCaptureOperation { Syntax: ExpressionSyntax expression } &&
                expression.Parent is SwitchStatementSyntax statement && statement.Expression == expression &&
                !switchesWithTests.Contains(expression.SpanStart))
                Append([SwitchInput(expression)]);
        }
        if (block.BranchValue is { } value)
            Append(Evaluate(value));
        if (switchStarts.TryGetValue(block.Ordinal, out var input))
            Append([SwitchInput(input)]);
        if (block.ConditionKind != ControlFlowConditionKind.None)
        {
            var test = SwitchCase(block.BranchValue);
            Append([new Visible(NodeKind.Condition, ConditionLabel(block.BranchValue, test),
                test is SwitchLabelSyntax ? range(markers.OriginalSpan(test.Span)) : OperationSource(block.BranchValue),
                IsCaseTest: test is not null || block.BranchValue?.Syntax.Parent is WhenClauseSyntax)]);
        }

        Route(block.ConditionalSuccessor);
        Route(block.FallThroughSuccessor);
        return;

        void Append(IEnumerable<Visible?> operations)
        {
            foreach (var candidate in operations)
            {
                if (candidate is not { } operation) continue;
                var next = Add(operation, block.IsReachable);
                edges.Add(new FlowEdge(last, next, IsReachable: block.IsReachable));
                last = next;
            }
        }

        void Route(ControlFlowBranch? branch)
        {
            if (branch is null)
                return;
            bool? when = block.ConditionKind == ControlFlowConditionKind.None ? null
                : branch.IsConditionalSuccessor == (block.ConditionKind == ControlFlowConditionKind.WhenTrue);
            var reachable = block.IsReachable && branch.Destination?.IsReachable != false;
            if (when is { } truth && block.BranchValue?.ConstantValue is { HasValue: true, Value: bool constant })
                reachable &= truth == constant;
            var label = when switch { true => "True", false => "False", _ => null };
            var destination = GetBranchDestination(branch, state.Context, reachable);
            edges.Add(new FlowEdge(last, destination, label, reachable));
        }
    }

    private string GetBranchDestination(ControlFlowBranch branch, int context, bool reachable)
    {
        var block = branch.Source;
        var source = OperationSource(block.BranchValue);
        string destination;
        switch (branch.Semantics)
        {
            case ControlFlowBranchSemantics.StructuredExceptionHandling:
                if (context != 0 && IsInside(block.EnclosingRegion, contexts[context]!.Region)
                    && !IsInsideFilter(block.EnclosingRegion, contexts[context]!.Region))
                    return contexts[context]!.Resume;
                return Add(new Visible(NodeKind.Exception, "フィルター不成立または例外探索を継続", source), reachable);

            case ControlFlowBranchSemantics.Throw:
            case ControlFlowBranchSemantics.Rethrow:
                var (finallyRegions, handler, unknownHandler) = ExceptionPath(block.EnclosingRegion, ExactExceptionType(block.BranchValue));
                if (handler is not null)
                {
                    var catchBlock = graph.Blocks[handler.FirstBlockOrdinal];
                    destination = Prepend(new Visible(NodeKind.Exception, "catch · " + handler.ExceptionType?.ToDisplayString(),
                        BlockSource(catchBlock)), GetBlock(new State(catchBlock.Ordinal, ContextAt(context, handler))), reachable);
                }
                else
                {
                    destination = Add(new Visible(NodeKind.Exception, unknownHandler
                        ? "実行時にハンドラーを探索" : "呼び出し元へ例外を伝播", null), reachable);
                }
                foreach (var region in finallyRegions.AsEnumerable().Reverse())
                    destination = RunFinally(region, destination, ContextAt(context, region.EnclosingRegion));
                source = TransferSource(block.BranchValue, SyntaxKind.ThrowStatement, SyntaxKind.ThrowExpression)
                    ?? RethrowSource(block);
                return Prepend(new Visible(NodeKind.Exception, branch.Semantics == ControlFlowBranchSemantics.Rethrow
                    ? "throw · 再スロー" : "throw", source), destination, reachable);

            case ControlFlowBranchSemantics.Regular:
            case ControlFlowBranchSemantics.Return:
                if (branch.Destination is { } next)
                {
                    destination = GetBlock(new State(next.Ordinal, ContextAt(context, next.EnclosingRegion)));
                    foreach (var region in branch.FinallyRegions.Reverse())
                        destination = RunFinally(region, destination, ContextAt(context, region.EnclosingRegion));
                    if (branch.Semantics == ControlFlowBranchSemantics.Return)
                        destination = Prepend(new Visible(NodeKind.Transfer, "return",
                            TransferSource(block.BranchValue, SyntaxKind.ReturnStatement) ?? source), destination, reachable);
                    return destination;
                }
                break;

            case ControlFlowBranchSemantics.ProgramTermination:
                return Add(new Visible(NodeKind.End, "実行を終了", source), reachable);
        }

        MarkPartial("Roslyn CFG に解決できない遷移があります。");
        return Add(new Visible(NodeKind.Exception, "遷移先を解決できません", source), reachable);
    }

    private SourceRange? RethrowSource(BasicBlock block)
    {
        // A bare throw has no BranchValue. Use its explicit marker, when present,
        // for precise navigation and fall back to the nearest operation otherwise.
        var marker = block.Operations.SelectMany(operation => operation.DescendantsAndSelf())
            .OfType<IInvocationOperation>().LastOrDefault(operation => operation.TargetMethod.Name == "ThrowStatement"
                && operation.TargetMethod.ContainingType.Name == markers.MarkerType);
        return OperationSource(marker) is { } range ? range with { Length = 5 } : BlockSource(block);
    }

    private INamedTypeSymbol? ExactExceptionType(IOperation? value)
    {
        while (true)
        {
            switch (value)
            {
                case IConversionOperation { IsImplicit: true, OperatorMethod: null } conversion:
                    value = conversion.Operand;
                    break;
                case IObjectCreationOperation { Type: INamedTypeSymbol type } when type.TypeKind != TypeKind.Error:
                    return type;
                case IFlowCaptureReferenceOperation reference when !ambiguousCaptures.Contains(reference.Id)
                     && captures.TryGetValue(reference.Id, out var original) && original is not IFlowCaptureReferenceOperation:
                    value = original;
                    break;
                default:
                    return value?.ConstantValue is { HasValue: true, Value: null }
                        ? markers.Model.Compilation.GetTypeByMetadataName("System.NullReferenceException") : null;
            }
        }
    }

    private static (List<ControlFlowRegion> Finally, ControlFlowRegion? Handler, bool UnknownHandler)
        ExceptionPath(ControlFlowRegion region, INamedTypeSymbol? thrownType)
    {
        var result = new List<ControlFlowRegion>();
        for (var current = region; current.EnclosingRegion is { } parent; current = parent)
        {
            if (current.Kind != ControlFlowRegionKind.Try)
                continue;
            if (parent.Kind == ControlFlowRegionKind.TryAndCatch)
            {
                foreach (var candidate in parent.NestedRegions.Skip(1))
                {
                    var handler = candidate.Kind == ControlFlowRegionKind.FilterAndHandler
                        ? candidate.NestedRegions.Single(nested => nested.Kind == ControlFlowRegionKind.Catch) : candidate;
                    // A matching filter may run before pending finally blocks.
                    // Its outcome and the type of a general thrown value need runtime data.
                    if (thrownType is null || handler.ExceptionType?.TypeKind == TypeKind.Error)
                        return ([], null, true);
                    if (!Catches(handler.ExceptionType, thrownType))
                        continue;
                    return candidate.Kind == ControlFlowRegionKind.FilterAndHandler
                        ? ([], null, true) : (result, handler, false);
                }
            }
            if (parent.Kind == ControlFlowRegionKind.TryAndFinally)
                result.Add(parent.NestedRegions.Single(nested => nested.Kind == ControlFlowRegionKind.Finally));
        }
        return (result, null, false);
    }

    private static bool Catches(ITypeSymbol? handler, INamedTypeSymbol thrown)
    {
        if (handler is null || handler.SpecialType == SpecialType.System_Object)
            return true;
        for (INamedTypeSymbol? type = thrown; type is not null; type = type.BaseType)
            if (SymbolEqualityComparer.Default.Equals(handler, type))
                return true;
        return false;
    }

    private static bool IsGeneratedCleanup(IInvocationOperation call)
    {
        if (!call.IsImplicit) return false;
        var method = call.TargetMethod;
        // Roslyn attaches generated cleanup calls to the resource, collection or lock expression.
        if (call.Syntax is ExpressionSyntax expression && expression.Parent is LockStatementSyntax @lock
            && @lock.Expression == expression)
        {
            return method.Name == "Exit" && method.ContainingType.ToDisplayString() == "System.Threading.Monitor"
                || method.Name == "Dispose" && method.ContainingType.ToDisplayString() == "System.Threading.Lock.Scope";
        }
        if (method.Name is not ("Dispose" or "DisposeAsync")) return false;
        return call.Syntax switch
        {
            VariableDeclaratorSyntax { Parent.Parent: UsingStatementSyntax } => true,
            VariableDeclaratorSyntax { Parent.Parent: LocalDeclarationStatementSyntax declaration } =>
                !declaration.UsingKeyword.IsKind(SyntaxKind.None),
            ExpressionSyntax { Parent: UsingStatementSyntax statement } resource => statement.Expression == resource,
            ExpressionSyntax { Parent: CommonForEachStatementSyntax statement } collection => statement.Expression == collection,
            _ => false
        };
    }

    private string RunFinally(ControlFlowRegion region, string resume, int parent)
    {
        if (generatedCleanupRegions.Contains(region)) return resume;
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var continuation = new Continuation(region, resume, parent);
        if (!contextIds.TryGetValue(continuation, out var id))
        {
            if (contexts.Count >= 4096)
            {
                MarkPartial("finally の継続先が上限（4096）に達しました。この先の経路を省略しています。");
                return Add(new Visible(NodeKind.Exception, "finally · 継続先の上限", null), true);
            }
            id = contexts.Count;
            contexts.Add(continuation);
            contextIds.Add(continuation, id);
            AddRegionEntries(region, id);
        }
        var block = graph.Blocks[region.FirstBlockOrdinal];
        return Prepend(new Visible(NodeKind.Exception, "finally", BlockSource(block)),
            GetBlock(new State(block.Ordinal, id)), block.IsReachable);
    }

    private int ContextAt(int context, ControlFlowRegion? region)
    {
        while (context != 0 && !IsInside(region, contexts[context]!.Region))
            context = contexts[context]!.Parent;
        return context;
    }

    private static bool IsInside(ControlFlowRegion? child, ControlFlowRegion parent)
    {
        for (var region = child; region is not null; region = region.EnclosingRegion)
            if (region == parent)
                return true;
        return false;
    }

    private static bool IsInsideFilter(ControlFlowRegion? child, ControlFlowRegion stop)
    {
        for (var region = child; region is not null && region != stop; region = region.EnclosingRegion)
            if (region.Kind == ControlFlowRegionKind.Filter)
                return true;
        return false;
    }

    private IEnumerable<Visible?> Evaluate(IOperation operation)
    {
        token.ThrowIfCancellationRequested();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (operation is IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation or ILocalFunctionOperation)
            yield break;
        if (operation is IFlowCaptureOperation capture && lvalues.Contains(capture.Id))
        {
            foreach (var item in EvaluateStorage(capture.Value))
                yield return item;
            // A spill of a compound lvalue is evaluated
            // before control enters the right-hand side of the assignment.
            if (compoundLocations.Contains(capture.Id))
                foreach (var item in AccessDynamicStorage(capture.Value, write: false))
                    yield return item;
            yield break;
        }

        if (operation is IFlowCaptureReferenceOperation location && lvalues.Contains(location.Id))
        {
            // Roslyn's ??= lowering reads the value through the captured
            // location once, then reuses the location for the optional write.
            if (!compoundLocations.Contains(location.Id))
                foreach (var item in AccessDynamicStorage(location, write: false))
                    yield return item;
            yield break;
        }

        if (operation is IAssignmentOperation assignment)
        {
            foreach (var item in EvaluateStorage(assignment.Target))
                yield return item;
            if (assignment.Target is not IFlowCaptureReferenceOperation && assignment is ICompoundAssignmentOperation)
                foreach (var item in AccessDynamicStorage(assignment.Target, write: false))
                    yield return item;
            foreach (var item in Evaluate(assignment.Value))
                yield return item;
            foreach (var item in AccessDynamicStorage(assignment.Target, write: true))
                yield return item;
            if (assignment is IDeconstructionAssignmentOperation)
                MarkPartial("分解代入に伴う暗黙のメソッドやセッターを完全には表示できません。");
            yield break;
        }

        if (operation is IIncrementOrDecrementOperation increment)
        {
            foreach (var item in EvaluateStorage(increment.Target))
                yield return item;
            foreach (var item in AccessDynamicStorage(increment.Target, write: false))
                yield return item;
            foreach (var item in AccessDynamicStorage(increment.Target, write: true))
                yield return item;
            yield break;
        }

        if (operation is IIsPatternOperation pattern && pattern.Pattern.DescendantsAndSelf()
            .Any(part => part is IRecursivePatternOperation or IListPatternOperation or ISlicePatternOperation))
        {
            foreach (var item in Evaluate(pattern.Value))
                yield return item;
            MarkPartial("複合パターン内部の判定・暗黙の getter は CFG の条件ノードに含まれ、一部の評価順を表示できません。");
            yield break;
        }

        foreach (var child in operation.ChildOperations)
            foreach (var item in Evaluate(child))
                yield return item;

        switch (operation)
        {
            case IInvocationOperation call when markers.MarkerType is { } marker && call.TargetMethod.ContainingType.Name == marker
                && call.TargetMethod.ContainingNamespace.IsGlobalNamespace:
                var keyword = call.TargetMethod.Name.Replace("Statement", "", StringComparison.Ordinal).ToLowerInvariant();
                // The marker maps to the start of the original transfer's keyword.
                var transfer = OperationSource(call)! with { Length = keyword.Length };
                if (keyword != "throw")
                    yield return new Visible(NodeKind.Transfer, keyword, transfer);
                break;
            case IInvocationOperation { TargetMethod.MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet }:
                break;
            case IInvocationOperation call:
                yield return Call(call.TargetMethod, call, Name(call.TargetMethod) + "()", call.IsVirtual, call.Instance?.Type);
                break;
            case IDynamicInvocationOperation or IFunctionPointerInvocationOperation or IDynamicObjectCreationOperation:
                yield return new Visible(NodeKind.Call, "呼び出し", OperationSource(operation));
                break;
            case IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation:
                yield return new Visible(NodeKind.Call, "動的メンバーアクセス", OperationSource(operation));
                break;
            case IObjectCreationOperation creation when creation.Constructor is { } ctor:
                yield return Call(ctor, creation, "new " + ctor.ContainingType.Name + "()");
                break;
            case IInvalidOperation:
                MarkPartial("未解決の式があります。呼び出し先や経路の一部は確認できません。");
                if (operation.Syntax is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
                    yield return new Visible(NodeKind.Call, "呼び出し · 未解決", OperationSource(operation));
                break;
            case IAwaitOperation:
                yield return new Visible(NodeKind.Transfer, "await", OperationSource(operation));
                break;
            case IReturnOperation { Kind: OperationKind.YieldReturn }:
                yield return new Visible(NodeKind.Transfer, "yield return", OperationSource(operation));
                break;
            case IEventAssignmentOperation { EventReference: IEventReferenceOperation reference } eventAssignment:
                var accessor = eventAssignment.Adds ? reference.Event.AddMethod : reference.Event.RemoveMethod;
                if (accessor is not null)
                    yield return Call(accessor, reference, Name(reference.Event) + (eventAssignment.Adds ? " · add" : " · remove"),
                        IsVirtual(accessor, reference.Instance), reference.Instance?.Type);
                break;
            case IImplicitIndexerReferenceOperation:
                MarkPartial("インデックス・範囲構文の暗黙の length／indexer 呼び出しを個別に表示できません。");
                break;
        }
    }

    private IEnumerable<Visible?> EvaluateStorage(IOperation target)
    {
        if (target is IFlowCaptureReferenceOperation)
            yield break;
        foreach (var child in target.ChildOperations)
            foreach (var item in Evaluate(child))
                yield return item;
    }

    private IEnumerable<Visible?> AccessDynamicStorage(IOperation target, bool write)
    {
        target = ResolveStorage(target);
        if (target is IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation)
            yield return new Visible(NodeKind.Call, write ? "書き込み" : "読み取り", OperationSource(target));
    }

    private static bool IsVirtual(IMethodSymbol method, IOperation? instance) =>
        instance?.Syntax.IsKind(SyntaxKind.BaseExpression) != true &&
        (method.IsVirtual || method.IsOverride || method.IsAbstract || method.ContainingType.TypeKind == TypeKind.Interface);

    private IOperation ResolveStorage(IOperation target)
    {
        if (target is not IFlowCaptureReferenceOperation reference || !lvalues.Contains(reference.Id) ||
            !captures.TryGetValue(reference.Id, out var captured))
            return target;
        if (!ambiguousCaptures.Contains(reference.Id))
            return captured;
        MarkPartial("分岐で変更される代入先があります。書き込み先を一意に解決できません。");
        return target;
    }

    private IReadOnlyList<INamedTypeSymbol>? candidateTypes;
    private IReadOnlyList<INamedTypeSymbol>? constructedCandidateTypes;
    private readonly Dictionary<(ISymbol Method, ISymbol Receiver), IReadOnlyList<ImplementationCandidate>> candidateCache = new();

    private const string ExclusionTimeout = "追跡対象外の正規表現が時間内に評価できなかったため、以降の呼び出しには除外設定を適用していません。";

    private static readonly SymbolDisplayFormat QualifiedTypeName = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    private Visible? Call(IMethodSymbol method, IOperation operation, string label, bool virtualCall = false, ITypeSymbol? receiver = null)
    {
        if (method.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion)
            return null;
        var compileTimeDefinition = method.ReducedFrom ?? method;
        var definition = compileTimeDefinition;
        if (excludedCalls is { Patterns.Count: > 0 } && !notices.Contains(ExclusionTimeout))
        {
            try
            {
                if (excludedCalls.Matches(definition.Name, definition.ContainingType.Name,
                    definition.ContainingType.ToDisplayString(QualifiedTypeName)))
                    return null;
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                MarkPartial(ExclusionTimeout);
            }
        }

        var dispatched = method.MethodKind == MethodKind.DelegateInvoke || method.IsStatic && method.IsAbstract;
        if (!dispatched && virtualCall && !method.IsSealed && !method.ContainingType.IsSealed)
        {
            var implementation = ResolveSealedReceiver(method, receiver);
            dispatched = implementation is null;
            if (implementation is not null) definition = implementation;
        }
        var target = method.MethodKind == MethodKind.DelegateInvoke ? null
            : ResolveTarget(dispatched ? compileTimeDefinition : definition);
        var declaration = (target is null || dispatched) && method.MethodKind != MethodKind.DelegateInvoke
            ? ResolveDeclaration(compileTimeDefinition) : null;
        var candidates = dispatched && target is null && declaration is not null && method.IsAbstract && !method.IsStatic
            ? FindImplementations(method, receiver) : null;
        return new Visible(NodeKind.Call, label, OperationSource(operation),
            target?.Offset, TargetFilePath: target?.Path, RequiresRuntimeDispatch: dispatched,
            TargetDeclaration: declaration, ImplementationCandidates: candidates);
    }

    private static IMethodSymbol? ResolveSealedReceiver(IMethodSymbol method, ITypeSymbol? receiver) =>
        receiver is INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: true } exactType
            ? ResolveClassImplementation(method, exactType) : null;

    private static IMethodSymbol? ResolveClassImplementation(IMethodSymbol method, INamedTypeSymbol exactType)
    {
        if (method.ContainingType.TypeKind != TypeKind.Class)
            return null;

        var slot = method.OriginalDefinition;
        for (INamedTypeSymbol? type = exactType; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, method.ContainingType))
                return method.IsAbstract ? null : method;

            foreach (var candidate in type.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                for (var overridden = candidate.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                    if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, slot))
                        return candidate.IsAbstract ? null : candidate;
            }
        }
        return null;
    }

    private IReadOnlyList<ImplementationCandidate> FindImplementations(IMethodSymbol method, ITypeSymbol? receiver)
    {
        if (receiver is not INamedTypeSymbol receiverType)
            return [];
        var key = ((ISymbol)method.ConstructedFrom, (ISymbol)receiverType);
        if (candidateCache.TryGetValue(key, out var cached)) return cached;
        if (candidateCache.Count >= 32) return [];

        var candidates = new Dictionary<(string? Path, int Offset), ImplementationCandidate>();
        IEnumerable<INamedTypeSymbol> types = candidateTypes ??= CandidateTypes();
        if (receiverType.IsGenericType)
            types = types.Concat(constructedCandidateTypes ??= ConstructedCandidateTypes().ToArray());
        foreach (var type in types)
        {
            token.ThrowIfCancellationRequested();
            if (receiverType.TypeKind == TypeKind.Interface)
            {
                foreach (var implementation in ResolveInterfaceImplementations(method.ConstructedFrom, type, receiverType))
                    AddCandidate(implementation);
            }
            else if (HasBaseType(type, receiverType))
            {
                AddCandidate(ResolveClassImplementation(method, type));
            }
        }
        return candidateCache[key] = candidates.Values.OrderBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Declaration.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.TargetMethod).Take(16).ToArray();

        void AddCandidate(IMethodSymbol? implementation)
        {
            if (implementation is not { IsAbstract: false } || ResolveTarget(implementation) is not { } target
                || ResolveDeclaration(implementation) is not { } declaration) return;
            var label = implementation.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            candidates.TryAdd((target.Path, target.Offset), new ImplementationCandidate(label, declaration, target.Offset, target.Path));
        }
    }

    private IEnumerable<IMethodSymbol> ResolveInterfaceImplementations(IMethodSymbol method, INamedTypeSymbol type,
        INamedTypeSymbol receiver)
    {
        var interfaces = type.AllInterfaces;
        var comparer = SymbolEqualityComparer.Default;
        if (interfaces.Contains(receiver, comparer))
        {
            if (type.FindImplementationForInterfaceMember(method) is IMethodSymbol exact)
                yield return exact;
            yield break;
        }
        if (!receiver.OriginalDefinition.TypeParameters.Any(parameter => parameter.Variance != VarianceKind.None) ||
            (type.IsGenericType && !IsClosedType(type)))
            yield break;

        var declaringInterface = method.ContainingType;
        var mapped = new HashSet<INamedTypeSymbol>(comparer);
        foreach (var actualReceiver in interfaces)
        {
            token.ThrowIfCancellationRequested();
            if (!HasVarianceConversion(actualReceiver, receiver)) continue;

            // An exact slot on the implementation takes precedence over compatible variant slots.
            if (interfaces.Contains(declaringInterface, comparer))
            {
                if (type.FindImplementationForInterfaceMember(method) is IMethodSymbol exact)
                    yield return exact;
                yield break;
            }

            foreach (var actual in actualReceiver.AllInterfaces.Prepend(actualReceiver))
            {
                token.ThrowIfCancellationRequested();
                if (!HasVarianceConversion(actual, declaringInterface) || !mapped.Add(actual)) continue;
                var slot = actual.GetMembers(method.Name).OfType<IMethodSymbol>()
                    .FirstOrDefault(member => comparer.Equals(member.OriginalDefinition, method.OriginalDefinition));
                if (slot is not null && type.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation)
                    yield return implementation;
            }
        }
    }

    private bool HasVarianceConversion(INamedTypeSymbol actual, INamedTypeSymbol expected) =>
        SymbolEqualityComparer.Default.Equals(actual.OriginalDefinition, expected.OriginalDefinition) &&
        ((CSharpCompilation)markers.Model.Compilation).ClassifyConversion(actual, expected)
            is { IsImplicit: true, IsReference: true };

    private static bool HasBaseType(INamedTypeSymbol type, INamedTypeSymbol receiver)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, receiver)) return true;
        return false;
    }

    private IReadOnlyList<INamedTypeSymbol> CandidateTypes()
    {
        var result = new List<INamedTypeSymbol>();
        var pending = new Stack<INamespaceOrTypeSymbol>();
        pending.Push(markers.Model.Compilation.Assembly.GlobalNamespace);
        for (var visited = 0; pending.Count > 0 && visited < 5000; visited++)
        {
            token.ThrowIfCancellationRequested();
            var item = pending.Pop();
            if (item is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, IsAbstract: false } type)
                result.Add(type);
            IEnumerable<INamespaceOrTypeSymbol> children = item is INamespaceSymbol ns
                ? ns.GetMembers() : item.GetTypeMembers();
            foreach (var child in children.OrderByDescending(member => member.MetadataName, StringComparer.Ordinal))
                pending.Push(child);
        }
        return result;
    }

    private IEnumerable<INamedTypeSymbol> ConstructedCandidateTypes()
    {
        // Do not invent generic arguments or scan other methods for allocations.
        var assembly = markers.Model.Compilation.Assembly;
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        IReadOnlyList<TextSpan>? errors = null;
        foreach (var operation in graph.OriginalOperation.Descendants().Take(10000))
        {
            token.ThrowIfCancellationRequested();
            if (operation is not IObjectCreationOperation { IsImplicit: false, Constructor: not null,
                    Type: INamedTypeSymbol { IsGenericType: true, IsAbstract: false } type } ||
                !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, assembly) ||
                !IsClosedType(type) || seen.Contains(type)) continue;

            errors ??= markers.Model.GetDiagnostics(graph.OriginalOperation.Syntax.Span, token)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.Location.SourceSpan).ToArray();
            if (errors.Any(error => operation.Syntax.Span.IntersectsWith(error))) continue;

            seen.Add(type);
            yield return type;
            if (seen.Count >= 256) yield break;
        }
    }

    private static bool IsClosedType(ITypeSymbol type) => type switch
    {
        INamedTypeSymbol named => named.TypeKind != TypeKind.Error && !named.IsUnboundGenericType &&
            named.TypeArguments.All(IsClosedType) && (named.ContainingType is null || IsClosedType(named.ContainingType)),
        IArrayTypeSymbol array => IsClosedType(array.ElementType),
        _ => type.TypeKind != TypeKind.TypeParameter
    };

    private (int Offset, string? Path)? ResolveTarget(IMethodSymbol method)
    {
        var definition = method.OriginalDefinition;
        definition = definition.PartialImplementationPart ?? definition;
        (int Offset, string? Path)? target = null;
        foreach (var reference in definition.DeclaringSyntaxReferences)
        {
            if (!TryGetTargetPath(reference.SyntaxTree, out var path))
                continue;

            var sameFile = path is null;

            // Only the instrumented caller tree has synthetic statements. Other trees
            // already use the exact declaration offset exposed in their own method list.
            if (sameFile ? !targets.Contains(markers.OriginalSpan(reference.Span).Start)
                : !SourceFile.HasBody(reference.GetSyntax(token)))
                continue;
            if (target is not null)
                return null;
            var offset = sameFile ? markers.OriginalSpan(reference.Span).Start : reference.Span.Start;
            target = (offset, path);
        }
        return target;
    }

    private SourceRange? ResolveDeclaration(IMethodSymbol method)
    {
        var definition = method.OriginalDefinition;
        definition = definition.PartialDefinitionPart ?? definition;
        SourceRange? target = null;
        foreach (var reference in definition.DeclaringSyntaxReferences)
        {
            if (!TryGetTargetPath(reference.SyntaxTree, out var path))
                continue;

            var span = reference.GetSyntax(token) switch
            {
                MethodDeclarationSyntax declaration => declaration.Identifier.Span,
                ConstructorDeclarationSyntax declaration => declaration.Identifier.Span,
                DestructorDeclarationSyntax declaration => declaration.Identifier.Span,
                LocalFunctionStatementSyntax declaration => declaration.Identifier.Span,
                AccessorDeclarationSyntax declaration => declaration.Keyword.Span,
                _ => default
            };
            if (span.IsEmpty) continue;
            if (target is not null) return null;
            target = path is null ? range(markers.OriginalSpan(span))
                : new SourceRange(span.Start, span.Length,
                    reference.SyntaxTree.GetLineSpan(span, token).StartLinePosition.Line + 1, path);
        }
        return target;
    }

    private bool TryGetTargetPath(SyntaxTree tree, out string? path)
    {
        path = tree == markers.Model.SyntaxTree ? null : tree.FilePath;
        return path is null || path.Length != 0 && navigableCSharpPaths?.Contains(path) == true;
    }

    private static string Name(ISymbol symbol) => symbol.ContainingType.Name + "." + symbol.Name;

    private Visible SwitchInput(ExpressionSyntax input) =>
        new(NodeKind.Condition, $"switch ({ShortLabel(input)})", range(markers.OriginalSpan(input.Span)));

    private static SyntaxNode? SwitchCase(IOperation? value) => value?.Syntax switch
    {
        CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax => value.Syntax,
        ExpressionSyntax { Parent: CaseSwitchLabelSyntax label } => label,
        PatternSyntax { Parent: CasePatternSwitchLabelSyntax label } => label,
        SwitchExpressionArmSyntax arm when value is IIsPatternOperation => arm,
        _ => null
    };

    private string ConditionLabel(IOperation? value, SyntaxNode? test)
    {
        if (value is null)
            return "条件";
        if (test is CaseSwitchLabelSyntax constant)
            return "case " + ShortLabel(constant.Value) + " に一致";
        if (test is CasePatternSwitchLabelSyntax pattern)
            return "case " + ShortLabel(pattern.Pattern) + " に一致";
        if (test is SwitchExpressionArmSyntax arm)
            return "case " + ShortLabel(arm.Pattern) + " に一致";
        if (value is IIsNullOperation)
            return "null";
        if (value.Syntax is ExpressionSyntax expression)
            return (expression.Parent is WhenClauseSyntax ? "when " : "") + ShortLabel(expression) + "";

        return value is IIsPatternOperation or IIsTypeOperation ? "パターン／型に一致" : "条件";
    }

    private static string ShortLabel(SyntaxNode syntax)
    {
        const int maxLength = 100;
        var label = new StringBuilder(maxLength + 1);
        var priorEnd = -1;
        foreach (var token in syntax.DescendantTokens())
        {
            if (token.IsMissing) continue;
            if (priorEnd >= 0 && priorEnd < token.SpanStart) label.Append(' ');
            priorEnd = token.Span.End;
            foreach (var ch in token.Text)
            {
                // Display control characters inside source tokens visibly, without
                // folding them into ordinary spaces or omitting consecutive spaces.
                label.Append(ch switch { '\r' => '␍', '\n' => '␊', '\t' => '␉', _ => ch });
                if (label.Length <= maxLength) continue;
                label.Length = char.IsHighSurrogate(label[maxLength - 2]) ? maxLength - 2 : maxLength - 1;
                return label.Append('…').ToString();
            }
        }
        return label.ToString();
    }

    private SourceRange? TransferSource(IOperation? operation, params SyntaxKind[] kinds) =>
        operation?.Syntax.AncestorsAndSelf().FirstOrDefault(node => kinds.Contains(node.Kind())) is { } syntax
            ? range(markers.OriginalSpan(syntax.Span)) : null;

    private SourceRange? OperationSource(IOperation? operation) => operation is null ? null : range(markers.OriginalSpan(operation.Syntax.Span));

    private SourceRange? BlockSource(BasicBlock block) => OperationSource(block.Operations.FirstOrDefault() ?? block.BranchValue);

    private string Add(Visible visible, bool reachable)
    {
        var id = "n" + nodes.Count;
        nodes.Add(new FlowNode(id, visible.Kind, visible.Label, visible.Source, visible.Target, reachable, visible.IsCaseTest, visible.TargetFilePath, visible.RequiresRuntimeDispatch, visible.TargetDeclaration, visible.ImplementationCandidates));
        return id;
    }

    private string Prepend(Visible node, string destination, bool reachable)
    {
        var id = Add(node, reachable);
        edges.Add(new FlowEdge(id, destination, IsReachable: reachable));
        return id;
    }

    private void MarkPartial(string notice)
    {
        partial = true;
        if (!notices.Contains(notice))
            notices.Add(notice);
    }

    private FlowDiagram Simplify()
    {
        var outgoing = edges.ToLookup(edge => edge.From);
        var redirects = new Dictionary<string, (string To, bool IsReachable)>();
        var emptyCycles = new HashSet<string>();
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (!transparent.Contains(node.Id) || redirects.ContainsKey(node.Id) || emptyCycles.Contains(node.Id))
                continue;
            var seen = new HashSet<string>();
            var path = new List<FlowEdge>();
            var last = node.Id;
            while (transparent.Contains(last) && !redirects.ContainsKey(last) && !emptyCycles.Contains(last)
                   && outgoing[last].Count() == 1 && outgoing[last].First() is { Label: null } edge)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(last))
                {
                    emptyCycles.Add(last);
                    break;
                }
                path.Add(edge);
                last = edge.To;
            }
            var destination = redirects.GetValueOrDefault(last, (To: last, IsReachable: true));
            for (var i = path.Count - 1; i >= 0; i--)
            {
                var edge = path[i];
                if (emptyCycles.Contains(edge.From))
                    destination = (edge.From, true);
                else
                    redirects[edge.From] = destination = (destination.To, destination.IsReachable && edge.IsReachable);
            }
        }
        var visible = nodes.Where(node => !redirects.ContainsKey(node.Id))
            .Select(node => emptyCycles.Contains(node.Id)
                ? node with { Kind = NodeKind.Transfer, Label = "繰り返し（表示対象の処理なし）" } : node).ToArray();
        var transitions = edges.Where(edge => !redirects.ContainsKey(edge.From))
            .Select(edge => redirects.TryGetValue(edge.To, out var destination)
                ? edge with { To = destination.To, IsReachable = edge.IsReachable && destination.IsReachable } : edge)
            .Distinct().ToArray();
        return new FlowDiagram(visible, transitions, notices.ToArray(), partial);
    }
}
