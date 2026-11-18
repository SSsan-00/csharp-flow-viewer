using System.Runtime.CompilerServices;
using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Flow.Analysis;

internal sealed class DiagramProjector(ControlFlowGraph graph, TransferMarkers markers,
    Func<TextSpan, SourceRange> range, IReadOnlySet<int> targets, CancellationToken token)
{
    private readonly record struct State(int Block, int Context);
    private sealed record Continuation(ControlFlowRegion Region, string Resume, int Parent);
    private readonly record struct Visible(NodeKind Kind, string Label, SourceRange? Source, int? Target = null);
    private sealed record RegionEntry(ControlFlowRegion Region, ControlFlowRegion? EnclosingFinally);

    private readonly Dictionary<State, string> states = new();
    private readonly Queue<State> pending = new();
    private readonly Dictionary<Continuation, int> contextIds = new();
    private readonly List<Continuation?> contexts = [null];
    private readonly List<FlowNode> nodes = [];
    private readonly List<FlowEdge> edges = [];
    private readonly HashSet<string> transparent = [];
    private readonly List<RegionEntry> regionEntries = [];
    private readonly Dictionary<CaptureId, IOperation> captures = new();
    private readonly HashSet<CaptureId> ambiguousCaptures = [];
    private ILookup<ControlFlowRegion?, BasicBlock> unreachableBlocks = null!;
    private readonly HashSet<CaptureId> lvalues = [];
    private readonly HashSet<CaptureId> compoundLocations = [];
    private readonly List<string> notices = [];
    private bool partial;

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
            Append(Evaluate(operation));
        if (block.BranchValue is { } value)
            Append(Evaluate(value));
        if (block.ConditionKind != ControlFlowConditionKind.None)
            Append([new Visible(NodeKind.Condition, ConditionLabel(block.BranchValue), OperationSource(block.BranchValue))]);

        Route(block.ConditionalSuccessor);
        Route(block.FallThroughSuccessor);
        return;

        void Append(IEnumerable<Visible> operations)
        {
            foreach (var operation in operations)
            {
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

    private string RunFinally(ControlFlowRegion region, string resume, int parent)
    {
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

    private IEnumerable<Visible> Evaluate(IOperation operation)
    {
        token.ThrowIfCancellationRequested();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (operation is IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation or ILocalFunctionOperation)
            yield break;
        if (operation is IFlowCaptureOperation capture && lvalues.Contains(capture.Id))
        {
            foreach (var item in EvaluateStorage(capture.Value))
                yield return item;
            // A spill of a compound or ref-returning lvalue is evaluated
            // before control enters the right-hand side of the assignment.
            if (compoundLocations.Contains(capture.Id) || IsRefReturningProperty(capture.Value))
                foreach (var item in AccessProperty(capture.Value, write: false))
                    yield return item;
            yield break;
        }

        if (operation is IFlowCaptureReferenceOperation location && lvalues.Contains(location.Id))
        {
            // Roslyn's ??= lowering reads the value through the captured
            // location once, then reuses the location for the optional write.
            if (!compoundLocations.Contains(location.Id) && !IsRefReturningProperty(location))
                foreach (var item in AccessProperty(location, write: false))
                    yield return item;
            yield break;
        }

        if (operation is IAssignmentOperation assignment)
        {
            foreach (var item in EvaluateStorage(assignment.Target))
                yield return item;
            if (assignment.Target is not IFlowCaptureReferenceOperation &&
                (assignment is ICompoundAssignmentOperation || IsRefReturningProperty(assignment.Target)))
                foreach (var item in AccessProperty(assignment.Target, write: false))
                    yield return item;
            foreach (var item in Evaluate(assignment.Value))
                yield return item;
            if (assignment is ICompoundAssignmentOperation { OperatorMethod: { } op })
                yield return Call(op, operation, op.Name);
            if (!IsRefReturningProperty(assignment.Target))
                foreach (var item in AccessProperty(assignment.Target, write: true))
                    yield return item;
            if (assignment is IDeconstructionAssignmentOperation)
                MarkPartial("分解代入に伴う暗黙のメソッドやセッターを完全には表示できません。");
            yield break;
        }

        if (operation is IIncrementOrDecrementOperation increment)
        {
            foreach (var item in EvaluateStorage(increment.Target))
                yield return item;
            foreach (var item in AccessProperty(increment.Target, write: false))
                yield return item;
            if (increment.OperatorMethod is { } op)
                yield return Call(op, operation, op.Name);
            if (!IsRefReturningProperty(increment.Target))
                foreach (var item in AccessProperty(increment.Target, write: true))
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
            case IInvocationOperation call:
                yield return Call(call.TargetMethod, call, Name(call.TargetMethod) + "()", call.IsVirtual, call.Instance?.Type);
                break;
            case IDynamicInvocationOperation or IFunctionPointerInvocationOperation or IDynamicObjectCreationOperation:
                yield return new Visible(NodeKind.Call, "呼び出し · 実行時に決定", OperationSource(operation));
                break;
            case IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation:
                yield return new Visible(NodeKind.Call, "動的メンバーアクセス · 実行時に決定", OperationSource(operation));
                break;
            case IObjectCreationOperation creation when creation.Constructor is { } ctor:
                yield return Call(ctor, creation, "new " + ctor.ContainingType.Name + "()");
                break;
            case IPropertyReferenceOperation:
                foreach (var item in AccessProperty(operation, write: false))
                    yield return item;
                break;
            case IConversionOperation { OperatorMethod: { } conversion }:
                yield return Call(conversion, operation, conversion.Name);
                break;
            case IBinaryOperation { OperatorMethod: { } binary }:
                yield return Call(binary, operation, binary.Name);
                break;
            case IUnaryOperation { OperatorMethod: { } unary }:
                yield return Call(unary, operation, unary.Name);
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

    private IEnumerable<Visible> EvaluateStorage(IOperation target)
    {
        if (target is IFlowCaptureReferenceOperation)
            yield break;
        foreach (var child in target.ChildOperations)
            foreach (var item in Evaluate(child))
                yield return item;
    }

    private IEnumerable<Visible> AccessProperty(IOperation target, bool write)
    {
        target = ResolveStorage(target);
        if (target is IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation)
        {
            yield return new Visible(NodeKind.Call, write ? "書き込み · 実行時に決定" : "読み取り · 実行時に決定", OperationSource(target));
            yield break;
        }
        if (target is not IPropertyReferenceOperation property)
            yield break;
        var accessor = write ? property.Property.SetMethod : property.Property.GetMethod;
        if (accessor is null)
            yield break;
        var label = Name(property.Property) + (write ? " · set" : " · get");
        yield return Call(accessor, property, label, IsVirtual(accessor, property.Instance), property.Instance?.Type);
    }

    private static bool IsVirtual(IMethodSymbol method, IOperation? instance) =>
        instance?.Syntax.IsKind(SyntaxKind.BaseExpression) != true &&
        (method.IsVirtual || method.IsOverride || method.IsAbstract || method.ContainingType.TypeKind == TypeKind.Interface);

    private bool IsRefReturningProperty(IOperation target) =>
        ResolveStorage(target) is IPropertyReferenceOperation { Property: { ReturnsByRef: true } };

    private IOperation ResolveStorage(IOperation target)
    {
        if (target is not IFlowCaptureReferenceOperation reference || !lvalues.Contains(reference.Id) ||
            !captures.TryGetValue(reference.Id, out var captured))
            return target;
        if (!ambiguousCaptures.Contains(reference.Id))
            return captured;
        MarkPartial("分岐で変更される代入先があります。セッターの呼び出し先を一意に解決できません。");
        return target;
    }

    private Visible Call(IMethodSymbol method, IOperation operation, string label, bool virtualCall = false, ITypeSymbol? receiver = null)
    {
        var dispatched = method.MethodKind == MethodKind.DelegateInvoke || method.IsStatic && method.IsAbstract ||
            virtualCall && !method.IsSealed && !method.ContainingType.IsSealed &&
            !(receiver?.IsSealed == true && SymbolEqualityComparer.Default.Equals(receiver, method.ContainingType));
        int? target = null;
        if (!dispatched)
        {
            var definition = method.ReducedFrom ?? method;
            definition = definition.PartialImplementationPart ?? definition;
            if (definition.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault(s => s.SyntaxTree == markers.Model.SyntaxTree) is { } syntax)
            {
                var declaration = syntax.GetSyntax(token);
                if (declaration is ArrowExpressionClauseSyntax && declaration.Parent is BasePropertyDeclarationSyntax property)
                    declaration = property;
                var offset = markers.OriginalSpan(declaration.Span).Start;
                if (targets.Contains(offset))
                    target = offset;
            }
        }
        return new Visible(NodeKind.Call, label + (dispatched ? " · 実行時に決定" : ""), OperationSource(operation), target);
    }

    private static string Name(ISymbol symbol) => symbol.ContainingType.Name + "." + symbol.Name;

    private string ConditionLabel(IOperation? value)
    {
        if (value is null)
            return "条件";
        if (value is IIsNullOperation)
            return "null？";
        if (value.Syntax is CaseSwitchLabelSyntax { Value: { } constant })
            return "case " + ShortLabel(constant) + " に一致？";
        if (value is IIsPatternOperation && value.Syntax is CasePatternSwitchLabelSyntax { Pattern: { } pattern })
            return "case " + ShortLabel(pattern) + " に一致？";
        if (value.IsImplicit || value.DescendantsAndSelf().Any(item => item is IInvocationOperation or IAssignmentOperation
            or IFlowCaptureReferenceOperation or IPropertyReferenceOperation or IDynamicInvocationOperation
            or IIncrementOrDecrementOperation or IObjectCreationOperation))
            return value is IIsPatternOperation or IIsTypeOperation ? "パターン／型に一致？" : "評価結果？";

        return ShortLabel(value.Syntax) + "？";
    }

    private static string ShortLabel(SyntaxNode syntax)
    {
        var label = string.Join(" ", syntax.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return label.Length <= 100 ? label : label[..99] + "…";
    }

    private SourceRange? TransferSource(IOperation? operation, params SyntaxKind[] kinds) =>
        operation?.Syntax.AncestorsAndSelf().FirstOrDefault(node => kinds.Contains(node.Kind())) is { } syntax
            ? range(markers.OriginalSpan(syntax.Span)) : null;

    private SourceRange? OperationSource(IOperation? operation) => operation is null ? null : range(markers.OriginalSpan(operation.Syntax.Span));

    private SourceRange? BlockSource(BasicBlock block) => OperationSource(block.Operations.FirstOrDefault() ?? block.BranchValue);

    private string Add(Visible visible, bool reachable)
    {
        var id = "n" + nodes.Count;
        nodes.Add(new FlowNode(id, visible.Kind, visible.Label, visible.Source, visible.Target, reachable));
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
        var redirects = new Dictionary<string, string>();
        foreach (var node in nodes)
        {
            if (!transparent.Contains(node.Id) || redirects.ContainsKey(node.Id))
                continue;
            var path = new HashSet<string>();
            var last = node.Id;
            while (transparent.Contains(last) && !redirects.ContainsKey(last) && path.Add(last)
                   && outgoing[last].Count() == 1 && outgoing[last].First() is { Label: null } edge
                   && edge.IsReachable)
                last = edge.To;
            var destination = redirects.GetValueOrDefault(last, last);
            foreach (var id in path.Where(id => id != destination))
                redirects[id] = destination;
        }
        var visible = nodes.Where(node => !redirects.ContainsKey(node.Id)).ToArray();
        var transitions = edges.Where(edge => !redirects.ContainsKey(edge.From))
            .Select(edge => edge with { To = redirects.GetValueOrDefault(edge.To, edge.To) }).Distinct().ToArray();
        return new FlowDiagram(visible, transitions, notices.ToArray(), partial);
    }
}
