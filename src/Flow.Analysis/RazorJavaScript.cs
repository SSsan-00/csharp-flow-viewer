using Acornima;
using Acornima.Ast;
using Flow.Model;
using JsProgram = Acornima.Ast.Program;

namespace Flow.Analysis;

/// <summary>元文書の範囲を保ったJavaScript。Razor式が残る断片は静的なJSとみなさない。</summary>
public sealed record RazorJavaScriptFragment(string Text, SourceRange Source, bool ContainsRazorCode = false,
    bool IsModule = false)
{
    internal IReadOnlyDictionary<int, SourceRange>? RenderingSlots { get; init; }
    internal IReadOnlyList<(int Slot, SourceRange Source, string Text)>? GeneratedStatements { get; init; }
    internal bool IsolatedClassic { get; init; }
    internal bool VisibleToInlineEvents { get; init; }
}

/// <summary>イベントの登録候補。条件付き登録や旧IE専用のAPIを確定した動作と区別する。</summary>
public sealed record RazorJavaScriptBinding(string Selector, string EventName, int? HandlerEntryId,
    SourceRange Source, string HandlerName, bool IsDelegated = false, bool IsConditional = false,
    bool IsLegacy = false);

/// <summary>HTTP送信の静的な要約。応答コールバックは送信直後の同期処理には展開しない。</summary>
public sealed record RazorJavaScriptRequest(int OwnerEntryId, string Client, string HttpMethod, string Url,
    string? Body, SourceRange Source, bool IsAsync, bool IsResolved, IReadOnlyList<int> CallbackEntryIds);

/// <summary>
/// Acornimaの構文木から関数、イベント登録、XHR送信を調べる。対象コードは実行しない。
/// 動的な要素や関数名は候補として残し、解決できないことを診断に表示する。
/// </summary>
public sealed class RazorJavaScript
{
    private readonly string pageText;
    private readonly string pagePath;
    private readonly bool collectSourceBrackets;
    private readonly List<MethodEntry> entries = [];
    private readonly Dictionary<int, Scope?> generatedStatementScopes = [];
    private readonly List<string> notices = [];
    private readonly List<RazorJavaScriptBinding> bindings = [];
    private readonly List<RazorJavaScriptRequest> requests = [];
    private readonly Dictionary<int, EntryData> data = [];
    private readonly Dictionary<Node, EntryData> functions = [];
    private readonly Dictionary<Node, Scope> scopes = [];
    private readonly Dictionary<Node, int> owners = [];
    private readonly HashSet<CallExpression> standaloneFocus = [];
    private readonly HashSet<Node> conditionalNodes = [];
    private readonly Dictionary<Node, int> requestNodes = [];
    private readonly List<XhrState> requestClients = [];
    private readonly object callbackLock = new();
    private bool callbacksDirty;
    private readonly Dictionary<(XhrState Client, int Owner), XhrUsage> xhrUsages = [];
    private readonly Scope globals = new(null);
    private readonly HashSet<int> windowFunctions = [];
    private readonly HashSet<int> explicitWindowFunctions = [];
    private readonly HashSet<int> classicFunctionDeclarations = [];
    private readonly List<(Scope Scope, ScriptUnit Unit, string Name, string Reference)> windowAliases = [];
    private readonly Dictionary<(Scope Scope, string Name), List<(ScriptUnit Unit, bool RequiresWindow)>> unchangedClassicBindings = [];
    private readonly Dictionary<string, SourceRange?> sourceOnlyDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<Scope, bool> isolatedClassicScopes = new();
    private readonly Dictionary<string, ScriptUnit> externalUnits = new(LocalJavaScriptFiles.PathComparer);
    private readonly Dictionary<string, List<SourceBracket>> sourceBrackets = new(LocalJavaScriptFiles.PathComparer);
    private readonly Dictionary<string, (ScriptUnit Unit, Scope Scope)> modules = new(LocalJavaScriptFiles.PathComparer);
    private int nextId = -2;

    public IReadOnlyList<MethodEntry> Entries => entries;
    internal IReadOnlyList<SourceBracket> GetBrackets(string path) =>
        sourceBrackets.TryGetValue(path, out var brackets) ? brackets : [];
    public IReadOnlyList<string> Notices => notices;
    public IReadOnlyList<RazorJavaScriptBinding> Bindings => bindings;
    public IReadOnlyList<RazorJavaScriptRequest> Requests
    {
        get
        {
            CompleteCallbacks();
            return requests;
        }
    }

    public RazorJavaScript(string pageText, string pagePath, IEnumerable<RazorJavaScriptFragment> fragments,
        CancellationToken token = default) : this(pageText, pagePath, fragments, null, token) { }

    internal RazorJavaScript(string pageText, string pagePath, IEnumerable<RazorJavaScriptFragment> fragments,
        Func<string, string, RazorJavaScriptFragment?>? loadImport, CancellationToken token,
        bool standalone = false, bool collectSourceBrackets = true)
    {
        this.pageText = pageText;
        this.pagePath = pagePath;
        this.collectSourceBrackets = collectSourceBrackets;
        var parsed = new List<(ScriptUnit Unit, Scope Scope)>();
        foreach (var fragment in fragments) IndexFragment(fragment);
        // Drop only known no-ops on an otherwise unique, hoisted function in the same classic script.
        foreach (var ((scope, name), bindings) in unchangedClassicBindings)
        {
            token.ThrowIfCancellationRequested();
            var hasGlobalWindow = !scope.IsDeclared("window");
            scope.KeepUniqueDeclaration(name, id => data[id] is { IsTopLevelClassicFunction: true } definition
                ? bindings.Count(binding => ReferenceEquals(binding.Unit, definition.Unit)
                    && (!binding.RequiresWindow || hasGlobalWindow))
                : 0);
        }
        unchangedClassicBindings.Clear();
        foreach (var (scope, unit, name, reference) in windowAliases)
        {
            token.ThrowIfCancellationRequested();
            if (!scope.IsDeclared("window") && scope.ResolveLocal(reference) is { } target
                && classicFunctionDeclarations.Contains(target) && ReferenceEquals(data[target].Unit, unit))
                scope.PublishWindowAlias(name, target);
        }
        windowAliases.Clear();
        foreach (var name in sourceOnlyDefinitions.Keys.ToArray())
            if (globals.DeclaresHere(name) || isolatedClassicScopes.Keys.Any(scope => scope.DeclaresHere(name)))
            {
                globals.Block(name);
                sourceOnlyDefinitions[name] = null;
            }
        // Modules bind exports only after declarations from all scripts are indexed.
        var moduleExports = modules.ToDictionary(pair => pair.Key, pair => BuildExports(pair.Value),
            LocalJavaScriptFiles.PathComparer);
        foreach (var (unit, scope) in parsed)
            foreach (var import in unit.Root.Body.OfType<ImportDeclaration>())
                BindImport(import, unit, scope);
        foreach (var (unit, _) in parsed)
            FindBindingsAndRequests(unit.Root, unit, token);

        void IndexFragment(RazorJavaScriptFragment fragment)
        {
            token.ThrowIfCancellationRequested();
            var path = fragment.Source.FilePath;
            var external = path is not null && (standalone || !LocalJavaScriptFiles.PathComparer.Equals(path, pagePath));
            if (external && externalUnits.ContainsKey(path!)) return;
            var diagnosticsBeforeParse = notices.Count;
            var unit = Parse(fragment, inline: false, token: token);
            if (unit is null && standalone && LocalJavaScriptFiles.PathComparer.Equals(path, pagePath) && !fragment.IsModule)
            {
                var classicFailure = notices.Skip(diagnosticsBeforeParse).ToArray();
                notices.RemoveRange(diagnosticsBeforeParse, notices.Count - diagnosticsBeforeParse);
                var module = Parse(fragment with { IsModule = true }, inline: false, token: token);
                if (module is null)
                {
                    notices.RemoveRange(diagnosticsBeforeParse, notices.Count - diagnosticsBeforeParse);
                    notices.AddRange(classicFailure);
                }
                else
                {
                    fragment = fragment with { IsModule = true };
                    unit = module;
                }
            }
            var name = $"JavaScript 初期化 L{fragment.Source.Line}";
            var scope = fragment.IsModule || fragment.IsolatedClassic ? new Scope(null) : globals;
            if (unit is null)
            {
                if (external || fragment.RenderingSlots is { Count: > 0 })
                {
                    var failedUnit = new ScriptUnit(fragment.Text, fragment.Text, fragment.Source, null!,
                        fragment.ContainsRazorCode, fragment.RenderingSlots);
                    var failed = AddEntry(name, null, failedUnit, scope);
                    failed.Failure = external ? "JavaScriptの構文を確定できません。"
                        : "Razorによる生成部分を除いたJavaScriptは構文を確定できません。";
                    if (external) externalUnits.Add(path!, failedUnit);
                }
                return;
            }

            if (!external && unit.RenderingSlots is { } slots)
                foreach (var slot in slots.Values) generatedStatementScopes.TryAdd(slot.Offset, null);
            if (external) externalUnits.Add(path!, unit);
            if (external && fragment.IsModule) modules.Add(path!, (unit, scope));
            if (fragment.IsolatedClassic && !fragment.IsModule)
                isolatedClassicScopes.Add(scope, fragment.VisibleToInlineEvents);
            var entry = AddEntry(name, unit.Root, unit, scope);
            scope.OwnerEntryId = entry.Entry.Id;
            Index(unit.Root, unit, scope, token, 0, conditional: false);
            if (fragment.GeneratedStatements is { } statements)
                foreach (var (slot, source, text) in statements)
                {
                    token.ThrowIfCancellationRequested();
                    if (generatedStatementScopes.TryGetValue(slot, out var insertion) && insertion is not null)
                        SaveGeneratedBrackets(source, text, data[insertion.OwnerEntryId].Body is not JsProgram, fragment.IsModule);
                }
            if (!fragment.IsModule)
            {
                // Only a classic script's top-level var (not let/const) creates a window property.
                foreach (var declaration in unit.Root.Body.OfType<VariableDeclaration>())
                    if (declaration.Kind == VariableDeclarationKind.Var)
                        foreach (var variable in declaration.Declarations)
                            if (variable.Id is Identifier identifier)
                            {
                                if (variable.Init is { } init && functions.TryGetValue(init, out var function))
                                    windowFunctions.Add(function.Entry.Id);
                                else if (variable.Init is null && !unit.ContainsRazor && unit.Recovery is null)
                                    NoteUnchangedClassicBinding(scope, identifier.Name, unit, requiresWindow: false);
                            }
            }
            parsed.Add((unit, scope));

            foreach (var import in unit.Root.Body.OfType<ImportDeclaration>())
            {
                var imported = LoadImport(import, unit);
                if (imported is not null) IndexFragment(imported);
            }
        }

        RazorJavaScriptFragment? LoadImport(ImportDeclaration import, ScriptUnit unit) =>
            import.Attributes.Count == 0 && import.Phase == ImportPhase.None && unit.Source.FilePath is { } origin
                ? loadImport?.Invoke(import.Source.Value, origin) : null;

        void BindImport(ImportDeclaration import, ScriptUnit unit, Scope scope)
        {
            var dependency = LoadImport(import, unit);
            var target = dependency?.Source.FilePath is { } importedPath
                ? moduleExports.GetValueOrDefault(importedPath) : null;
            if (target is null)
                Notice($"L{Range(unit, import).Line}: 静的インポートをローカルのJavaScriptへ解決できません: {import.Source.Value}");
            foreach (var specifier in import.Specifiers)
            {
                var name = specifier.Local.Name;
                if (specifier is ImportNamespaceSpecifier && target is not null)
                    scope.Namespaces[name] = target;
                var exportedName = specifier switch
                {
                    ImportSpecifier named => ModuleName(named.Imported),
                    ImportDefaultSpecifier => "default",
                    _ => null
                };
                scope.Declare(name, exportedName is not null ? target?.GetValueOrDefault(exportedName) : null);
                scope.RememberExplicitFocusReceiver(name);
            }
        }
    }

    private void SaveGeneratedBrackets(SourceRange source, string text, bool insideFunction, bool isModule)
    {
        var (parser, delimiters) = BracketParser(insideFunction);
        try
        {
            if (isModule && !insideFunction) parser.ParseModule(text);
            else parser.ParseScript(text);
        }
        catch (Exception failure) when (failure is ParseErrorException or InsufficientExecutionStackException) { return; }
        if (delimiters is { Count: > 0 })
            SaveBrackets(delimiters, source, pageText.AsSpan(source.Offset, source.Length), null);
    }

    private static string? ModuleName(Expression expression) => expression switch
    {
        Identifier identifier => identifier.Name,
        StringLiteral literal => literal.Value,
        _ => null
    };

    private Dictionary<string, int?> BuildExports((ScriptUnit Unit, Scope Scope) module)
    {
        var result = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var statement in module.Unit.Root.Body)
        {
            switch (statement)
            {
                case ExportDefaultDeclaration { Declaration: FunctionDeclaration { Id: { } id } }:
                    result["default"] = FindLocal(id.Name);
                    break;
                case ExportDefaultDeclaration { Declaration: IFunction } byDefault:
                    result["default"] = !module.Scope.Dynamic && functions.TryGetValue(byDefault.Declaration, out var anonymous)
                        ? anonymous.Entry.Id : null;
                    break;
                case ExportDefaultDeclaration { Declaration: Identifier identifier }:
                    result["default"] = FindLocal(identifier.Name);
                    break;
                case ExportNamedDeclaration { Source: null } named:
                    if (named.Declaration is FunctionDeclaration { Id: { } function })
                        result[function.Name] = FindLocal(function.Name);
                    if (named.Declaration is VariableDeclaration variables)
                        foreach (var variable in variables.Declarations)
                            if (variable.Id is Identifier name) result[name.Name] = FindLocal(name.Name);
                    foreach (var specifier in named.Specifiers)
                        if (ModuleName(specifier.Exported) is { } exported && ModuleName(specifier.Local) is { } local)
                            result[exported] = FindLocal(local);
                    break;
            }
        }
        return result;

        int? FindLocal(string localName) => module.Scope.ResolveLocal(localName) is { } target
            && ReferenceEquals(data[target].Unit, module.Unit) ? target : null;
    }

    internal JavaScriptSourceFile? OpenScript(string path,
        Func<int, CancellationToken, FlowDiagram>? projectFlow = null)
    {
        if (!externalUnits.TryGetValue(path, out var unit)) return null;
        return new JavaScriptSourceFile(path, unit.OriginalText, this, projectFlow);
    }

    internal (int? Method, string? Path) ResolveNavigationTarget(int? id)
    {
        if (id is not { } key || !data.TryGetValue(key, out var target)) return (null, null);
        var source = target.Entry.Source;
        var path = source.FilePath ?? pagePath;
        return (externalUnits.ContainsKey(path) ? source.Offset : key, path);
    }

    /// <summary>onclick/onchangeなどの属性を、returnを許す関数本体として解析する。</summary>
    public int AddInlineHandler(string name, string code, SourceRange source, bool containsRazorCode = false,
        CancellationToken token = default) =>
        AddInlineHandler(name, InlineScriptText.Decode(code, token), source, containsRazorCode, token);

    internal int AddInlineHandler(string name, InlineScriptText decoded, SourceRange source, bool containsRazorCode,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var fragment = new RazorJavaScriptFragment(decoded.Text, source, containsRazorCode);
        var unit = Parse(fragment, inline: true, token, decoded);
        if (unit is null)
        {
            var failed = AddEntry(name, null, new ScriptUnit(decoded.Text, decoded.Encoded, source, null!, containsRazorCode),
                new Scope(globals));
            failed.Failure = "JavaScriptを静的に解析できません。診断を確認してください。";
            return failed.Entry.Id;
        }
        var scope = new Scope(globals) { InlineEvent = true };
        var entry = AddEntry(name, unit.Root, unit, scope);
        scope.OwnerEntryId = entry.Entry.Id;
        // インラインイベントのeventはブラウザーが渡す引数として扱う。
        scope.Declare("event", null);
        Index(unit.Root, unit, scope, token, 0, conditional: false);
        FindBindingsAndRequests(unit.Root, unit, token);
        return entry.Entry.Id;
    }

    /// <summary>選択されたJS関数だけの制御フローを作り、大きなページの全展開を避ける。</summary>
    public FlowDiagram Analyze(int entryId, CancellationToken token = default) => Projector(entryId, false, token).Build();

    internal (FlowDiagram Flow, IReadOnlySet<int> ExpandedReadyCallbacks) AnalyzeRazor(int entryId, CancellationToken token)
    {
        var projector = Projector(entryId, true, token);
        return (projector.Build(), projector.ExpandedReadyCallbacks);
    }

    private RazorJavaScriptProjector Projector(int entryId, bool omitBrowserDetails, CancellationToken token)
    {
        if (!data.TryGetValue(entryId, out var entry))
            throw new ArgumentOutOfRangeException(nameof(entryId));
        return new RazorJavaScriptProjector(this, entry, token, omitBrowserDetails);
    }

    internal sealed record GeneratedBranch(FlowDiagram? ClientFlow, string? Preview);

    internal GeneratedBranch? AnalyzeGeneratedBranch(int controlStart, string text, SourceRange source, int maxNodes,
        CancellationToken token, RazorMarkupDocument? dynamicOutput = null, bool unfoldMixedLoop = false,
        bool hasIterationLineBreak = false)
    {
        if (!generatedStatementScopes.TryGetValue(controlStart, out var scope)
            || unfoldMixedLoop && scope is null) return null;
        var owner = scope is not null ? data[scope.OwnerEntryId] : null;
        var insideFunction = owner is not null && owner.Body is not JsProgram;
        JsProgram? parsed = null;
        SourceRange? unresolvedTail = null;
        try
        {
            if (text.Length == source.Length)
            {
                try { parsed = new Parser(new ParserOptions { AllowReturnOutsideFunction = insideFunction }).ParseScript(text); }
                catch (ParseErrorException) { }
            }
            if (parsed is null)
            {
                if (!unfoldMixedLoop || dynamicOutput is null
                    || RazorScriptBoundaries.ReadExplicitPrefix(text, insideFunction, token) is not { } partial)
                    return null;
                var remainder = source.Length - partial.End;
                if (remainder <= 0) return null;
                unresolvedTail = dynamicOutput.Range(source.Offset + partial.End, remainder);
                source = source with { Length = partial.End };
                text = text[..partial.End];
                parsed = partial.Root;
            }
        }
        catch (InsufficientExecutionStackException) { return null; }
        var root = parsed;
        var unit = new ScriptUnit(text, dynamicOutput?.SourcePreview(source.Offset, source.Length) ?? text, source, root, unfoldMixedLoop);
        var entry = new EntryData(new MethodEntry(0, "", "", source), root, unit, new Scope(null));
        const string fixedEvent = "change 発生時（候補）";
        const string dynamicEvent = "change 発生時（候補・対象要素未確定）";
        var quotedValues = unfoldMixedLoop && dynamicOutput is not null
            ? Descendants(root).OfType<StringLiteral>().Where(ContainsRazor).OrderBy(literal => literal.Start)
                .Where(literal =>
                {
                    var range = Range(unit, literal);
                    return dynamicOutput.HasOnlyQuotedReferences(range, range);
                }).ToArray()
            : [];
        // Opaque generated code can change the outer bindings even when the surrounding JS parses.
        var blocked = unresolvedTail is null && scope is { HasDynamicBinding: false }
            && (!unfoldMixedLoop || OnlyQuotedValues(root))
            ? RazorGeneratedBindings.Changes(root, token) : null;
        var changeHandlers = new Dictionary<SourceRange, (Expression Handler, bool DynamicSelector)>();
        var graph = new RazorJavaScriptProjector(this, entry, token, omitBrowserDetails: true, Describe, IsHiddenHandler,
            unfoldMixedLoop ? OpaqueExpression : null, unresolvedTail).Build();
        if (changeHandlers.Count > 0) graph = WithChangeCallbacks(graph);
        if (dynamicOutput is not null && !unfoldMixedLoop && !graph.Edges.Any(edge => edge.Label == dynamicEvent))
            return null;
        var conditions = graph.Nodes.Any(node => node.Kind == NodeKind.Condition);
        var standalone = (!graph.IsPartial || unfoldMixedLoop) && root.Body is { Count: > 0 } statements
            && (text[statements[^1].End - 1] == ';' || hasIterationLineBreak && CanEndAtOwningScriptBoundary(owner, controlStart))
            && text[statements[0].Start] is not ('(' or '[' or '`' or '/' or '+' or '-' or '.')
            && graph.Nodes.Any(node => node.Kind is NodeKind.Call or NodeKind.Transfer);
        return new((conditions && unresolvedTail is null || standalone) ? graph : null, Preview());

        static bool CanEndAtOwningScriptBoundary(EntryData? owner, int start)
        {
            if (owner?.Unit.RenderingSlots is not { } slots) return false;
            var unit = owner.Unit;
            foreach (var slot in slots.Values)
            {
                if (slot.Offset != start) continue;
                var rest = unit.OriginalText.AsSpan(slot.Offset + slot.Length - unit.Source.Offset).TrimStart();
                return rest.IsEmpty || rest[0] is '}' or ';';
            }
            return false;
        }

        bool ContainsRazor(Node node) => dynamicOutput?.IsDynamic(source.Offset + node.Start, node.End - node.Start) == true;

        bool OnlyQuotedValues(Node node)
        {
            if (dynamicOutput is null) return true;
            var cursor = node.Start;
            foreach (var value in quotedValues)
            {
                if (value.Start < cursor) continue;
                if (value.End > node.End) break;
                if (dynamicOutput.IsDynamic(source.Offset + cursor, value.Start - cursor)) return false;
                cursor = value.End;
            }
            return !dynamicOutput.IsDynamic(source.Offset + cursor, node.End - cursor);
        }

        SourceRange? OpaqueExpression(Expression expression)
        {
            if (!ContainsRazor(expression)) return null;
            if (OnlyQuotedValues(expression) && (expression is not CallExpression call || !ContainsRazor(call.Callee)
                || scope is not null && blocked is not null && GeneratedChangeHandler(call, scope, blocked) is not null))
                return null;
            return dynamicOutput!.CoverGeneratedExpressions(Range(unit, expression));
        }

        string? Preview()
        {
            if (graph.IsPartial || root.Body.Any(DeclaresFunction)) return null;
            var relevant = graph.Nodes.Where(node => node.Kind is not (NodeKind.Start or NodeKind.End or NodeKind.Junction)
                    && node.Source is not null)
                .OrderBy(node => node.Source!.Offset)
                .Select(node => node.Kind == NodeKind.Condition ? $"条件: {node.Label}" : node.Label)
                .Distinct(StringComparer.Ordinal).Take(4).ToArray();
            if (relevant.Length > 0)
                return string.Join(" · ", relevant.Take(3)) + (relevant.Length > 3 ? "…" : "");
            return root.Body.All(IsBookkeeping) ? "" : null;
        }

        static bool DeclaresFunction(Statement node) => node is FunctionDeclaration
            || node is VariableDeclaration declaration && declaration.Declarations.Any(variable => variable.Init is IFunction)
            || node is ExpressionStatement { Expression: AssignmentExpression { Right: IFunction } };

        bool IsBookkeeping(Node node) => node switch
        {
            EmptyStatement or UpdateExpression => true,
            BlockStatement block => block.Body.All(IsBookkeeping),
            VariableDeclaration declaration => declaration.Declarations.All(variable => variable.Init is not IFunction),
            ExpressionStatement expression => IsBookkeeping(expression.Expression),
            SequenceExpression sequence => sequence.Expressions.All(IsBookkeeping),
            CallExpression call => Describe(call).Omit,
            AssignmentExpression assignment => assignment.Right is not IFunction &&
                (assignment.Right is not AssignmentExpression nested || IsBookkeeping(nested)),
            _ => false
        };

        CallProjection Describe(CallExpression call)
        {
            if (scope is null || blocked is null) return default;
            if (GeneratedChangeHandler(call, scope, blocked) is { } handler)
                changeHandlers.TryAdd(Range(unit, call), handler);
            return DescribeCall(call);
        }

        CallProjection DescribeCall(CallExpression call)
        {
            if (scope is null || blocked is null || ContainsRazor(call.Callee)) return default;
            var target = Resolve(call.Callee, scope, blocked);
            return new(target, target is null && RazorBrowserCalls.IsRoutine(call, scope, blocked, blocked.IsStandaloneFocus(call)));
        }

        (Expression Handler, bool DynamicSelector)? GeneratedChangeHandler(CallExpression call, Scope context,
            RazorGeneratedBindings bindings)
        {
            if (call is not { Optional: false, Callee: MemberExpression { Optional: false, Object: CallExpression
                { Optional: false, Callee: Identifier { Name: "$" or "jQuery" } jquery, Arguments: [StringLiteral selector] } } member }
                || PropertyName(member) != "bind" || call.Arguments.Count is < 2 or > 3
                || string.IsNullOrWhiteSpace(selector.Value) || selector.Value.TrimStart().StartsWith('<')
                || !IsJQuery(member.Object, context, bindings)
                || isolatedClassicScopes.Keys.Any(other => other.Dynamic || other.DeclaresHere(jquery.Name))
                || StringValue(call.Arguments[0]) is not { } events
                || !events.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(name => name.Split('.')[0] == "change"))
                return null;
            var dynamicSelector = ContainsRazor(selector);
            if (dynamicOutput is not null
                && (dynamicSelector
                    ? !selector.Value.StartsWith('#') || !dynamicOutput.HasOnlyQuotedReferences(
                        unfoldMixedLoop ? Range(unit, call) : source, Range(unit, selector))
                    : !unfoldMixedLoop || ContainsRazor(call)))
                return null;
            return (call.Arguments[^1], dynamicSelector);
        }

        FlowDiagram WithChangeCallbacks(FlowDiagram flow)
        {
            var nodes = flow.Nodes.ToList();
            var edges = flow.Edges.ToList();
            foreach (var registration in flow.Nodes)
            {
                token.ThrowIfCancellationRequested();
                if (nodes.Count >= maxNodes) break;
                if (registration is not { Kind: NodeKind.Call, Source: { } range }
                    || !changeHandlers.TryGetValue(range, out var callbackInfo)) continue;
                var (handler, dynamicSelector) = callbackInfo;
                var eventLabel = dynamicSelector ? dynamicEvent : fixedEvent;
                var prefix = registration.Id + "_change_";
                if (handler is IFunction callback)
                {
                    var localNames = callback.Params.SelectMany(JavaScriptWriteTargets.Leaves).OfType<Identifier>()
                        .Select(name => name.Name).ToHashSet(StringComparer.Ordinal);
                    if (callback.Id is { } name) localNames.Add(name.Name);
                    var body = new EntryData(entry.Entry, callback.Body, unit, entry.Scope);
                    var projected = new RazorJavaScriptProjector(this, body, token, omitBrowserDetails: true,
                        call => MentionsLocal(call.Callee) ? default : DescribeCall(call),
                        member => !MentionsLocal(member) && IsHiddenHandler(member),
                        unfoldMixedLoop ? OpaqueExpression : null).Build();
                    if (nodes.Count + projected.Nodes.Count - 1 > maxNodes) continue;
                    var start = projected.Nodes.Single(node => node.Kind == NodeKind.Start).Id;
                    nodes.AddRange(projected.Nodes.Where(node => node.Id != start).Select(node => node with { Id = prefix + node.Id }));
                    edges.AddRange(projected.Edges.Select(edge => edge.From == start
                        ? new FlowEdge(registration.Id, prefix + edge.To, eventLabel)
                        : edge with { From = prefix + edge.From, To = prefix + edge.To }));

                    bool MentionsLocal(Expression expression) => localNames.Count > 0
                        && Descendants(expression).OfType<Identifier>().Any(identifier => localNames.Contains(identifier.Name));
                }
                else if (Resolve(handler, scope!, blocked!) is { } target)
                {
                    var navigation = ResolveNavigationTarget(target);
                    nodes.Add(new FlowNode(prefix + "call", NodeKind.Call, Slice(unit, handler) + "()", Range(unit, handler),
                        navigation.Method, TargetFilePath: navigation.Path));
                    edges.Add(new FlowEdge(registration.Id, prefix + "call", eventLabel));
                }
            }
            return flow with { Nodes = nodes, Edges = edges };
        }

        bool IsHiddenHandler(MemberExpression member) => scope is not null && blocked is not null
            && RazorBrowserCalls.IsReadyStateHandler(member, scope, blocked);

        int? Resolve(Expression callee, Scope context, RazorGeneratedBindings bindings)
        {
            if (!bindings.Allows(callee) || ResolveFunctionReference(callee, context) is not { } target) return null;
            var globalName = callee switch
            {
                Identifier identifier => identifier.Name,
                MemberExpression { Object: Identifier { Name: "window" } } member => PropertyName(member),
                _ => null
            };
            return globalName is not null && context.DeclarationScope(globalName) == globals
                && isolatedClassicScopes.Keys.Any(other => other.Dynamic || other.DeclaresHere(globalName)) ? null : target;
        }
    }

    internal (SourceRange Range, IReadOnlyList<SourceRange> NestedFunctions)? ScriptRenderingScope(int entryId,
        IReadOnlySet<int> expandedReadyCallbacks)
    {
        var entry = data[entryId];
        if (entry.Unit.RenderingSlots is not { Count: > 0 }) return null;
        var range = entry.Body is JsProgram or null ? entry.Unit.Source : entry.Entry.Source;
        var nested = entry.Body is null ? [] : Descendants(entry.Body)
            .Where(node => node is IFunction && (!functions.TryGetValue(node, out var callback)
                || !expandedReadyCallbacks.Contains(callback.Entry.Id)))
            .Select(node => Range(entry.Unit, node)).ToArray();
        return (range, nested);
    }

    private ScriptUnit? Parse(RazorJavaScriptFragment fragment, bool inline, CancellationToken token,
        InlineScriptText? inlineText = null)
    {
        if (fragment.ContainsRazorCode)
        {
            Notice($"L{fragment.Source.Line}: Razorを含むJavaScriptは生成結果と動的な値を確定できません。抽出した処理は候補です。");
        }
        try
        {
            var (parser, delimiters) = BracketParser(inline, collectSourceBrackets);
            var text = fragment.Text;
            var renderingSlots = fragment.RenderingSlots;
            var canOfferLocalDefinition = !fragment.IsModule && !fragment.IsolatedClassic
                && LocalJavaScriptFiles.PathComparer.Equals(fragment.Source.FilePath, pagePath);
            JsProgram root;
            JavaScriptRecovery? recovery = null;
            try
            {
                root = fragment.IsModule ? parser.ParseModule(fragment.Text) : parser.ParseScript(fragment.Text);
            }
            catch (ParseErrorException error)
            {
                Notice($"L{fragment.Source.Line + Math.Max(0, error.LineNumber - 1)}: JavaScript構文エラー ({error.Error.Code})。");
                if (inline || fragment.IsModule) return null;
                if (renderingSlots is { Count: > 0 } generatedSlots)
                {
                    if (canOfferLocalDefinition && RazorScriptBoundaries.TryParseGeneratedValues(
                        parser, text, generatedSlots, error.Error, token) is { } projectedValues)
                        IndexDefinitionCandidates(projectedValues.Root, generatedSlots,
                            RazorScriptBoundaries.IndependentSlots(projectedValues.Root), projectedValues.Offsets);
                    return null;
                }
                recovery = JavaScriptRecovery.TryParse(fragment.Text, error.Error, token);
                if (recovery is null) return null;
                root = recovery.Program;
                Notice($"L{fragment.Source.Line}: 構文エラー箇所を補って静的な定義と呼び出しのみを解析しました。");
            }
            if (renderingSlots is { Count: > 0 } slots)
            {
                var statementSlots = RazorScriptBoundaries.IndependentSlots(root);
                if (slots.Keys.Any(offset => !statementSlots.Contains(offset)))
                {
                    if (!fragment.IsModule
                        && RazorScriptBoundaries.RestoreStatementAsi(text, root, slots, statementSlots, token) is { } restored
                        && ParseWithIndependentSlots(restored.Text, restored.Slots) is { } reparsed)
                    {
                        root = reparsed;
                        text = restored.Text;
                        renderingSlots = restored.Slots;
                    }
                    else
                    {
                        Notice($"L{fragment.Source.Line}: Razor生成部分の境界を独立したJavaScript文として確定できません。");
                        if (canOfferLocalDefinition) IndexDefinitionCandidates(root, slots, statementSlots);
                        return null;
                    }
                }
            }
            var projected = inlineText?.Encoded ?? fragment.Text;
            var original = fragment.Source.Offset >= 0 && fragment.Source.Offset + projected.Length <= pageText.Length
                && (fragment.Source.FilePath is null || fragment.Source.FilePath == pagePath)
                ? pageText.Substring(fragment.Source.Offset, projected.Length) : projected;
            if (recovery is null && delimiters is { Count: > 0 })
                SaveBrackets(delimiters, fragment.Source, original, inlineText);
            return new ScriptUnit(text, original, fragment.Source, root, fragment.ContainsRazorCode,
                renderingSlots, inlineText, recovery);

            void IndexDefinitionCandidates(JsProgram candidate, IReadOnlyDictionary<int, SourceRange> candidateSlots,
                IReadOnlySet<int> independent, IReadOnlySet<int>? generatedValues = null)
            {
                foreach (var (name, source) in RazorScriptBoundaries.SourcesOutsideUnresolvedSlots(
                    fragment, candidate, candidateSlots, independent, token, generatedValues))
                    if (!sourceOnlyDefinitions.TryAdd(name, source)) sourceOnlyDefinitions[name] = null;
            }

            JsProgram? ParseWithIndependentSlots(string candidate, IReadOnlyDictionary<int, SourceRange> candidateSlots)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    delimiters?.Clear();
                    var parsed = parser.ParseScript(candidate);
                    var independent = RazorScriptBoundaries.IndependentSlots(parsed);
                    return candidateSlots.Keys.All(independent.Contains) ? parsed : null;
                }
                catch (ParseErrorException) { return null; }
            }
        }
        catch (InsufficientExecutionStackException)
        {
            Notice($"L{fragment.Source.Line}: JavaScriptの入れ子が深いため解析できません。");
            return null;
        }
    }

    private static (Parser Parser, List<(int Offset, char Value, bool CanFold)>? Delimiters) BracketParser(
        bool allowReturn, bool collect = true)
    {
        if (!collect) return (new Parser(new ParserOptions { AllowReturnOutsideFunction = allowReturn }), null);
        var delimiters = new List<(int Offset, char Value, bool CanFold)>();
        return (new Parser(new ParserOptions { AllowReturnOutsideFunction = allowReturn, OnToken = TrackBracket }), delimiters);

        void TrackBracket(in Token current)
        {
            if (current.Kind != TokenKind.Punctuator || current.Value is not string value) return;
            if (value == "${")
                delimiters.Add((current.Start + 1, '{', false));
            else if (value is "(" or ")" or "[" or "]" or "{" or "}")
                delimiters.Add((current.Start, value[0], value == "{"));
        }
    }

    private void SaveBrackets(IEnumerable<(int Offset, char Value, bool CanFold)> delimiters,
        SourceRange range, ReadOnlySpan<char> original, InlineScriptText? inlineText)
    {
        var path = range.FilePath ?? pagePath;
        if (!sourceBrackets.TryGetValue(path, out var result)) sourceBrackets[path] = result = [];
        var pending = new Stack<(int Offset, char Value, bool CanFold)>();
        foreach (var item in delimiters)
        {
            if (item.Value is '(' or '[' or '{')
            {
                pending.Push(item);
                continue;
            }
            if (!pending.TryPop(out var open) || item.Value != (open.Value switch { '(' => ')', '[' => ']', _ => '}' }))
            {
                pending.Clear();
                continue;
            }
            var first = inlineText?.EncodedOffset(open.Offset) ?? open.Offset;
            var last = inlineText?.EncodedOffset(item.Offset) ?? item.Offset;
            if (first >= 0 && first < last && last < original.Length && original[first] == open.Value && original[last] == item.Value)
                result.Add(new SourceBracket(range.Offset + first, range.Offset + last, open.CanFold && inlineText is null));
        }
    }

    private EntryData AddEntry(string name, Node? body, ScriptUnit unit, Scope scope, Node? declaration = null)
    {
        var source = body is JsProgram or null ? unit.Source : Range(unit, body);
        var entry = new MethodEntry(nextId--, name, name, source)
        {
            NavigationSource = declaration is null ? source : Range(unit, declaration)
        };
        entries.Add(entry);
        var detail = new EntryData(entry, body, unit, scope, entries.Count - 1);
        data.Add(entry.Id, detail);
        return detail;
    }

    private void RenameEntry(EntryData entry, string name) =>
        entries[entry.Index] = entry.Entry = entry.Entry with { Name = name, Signature = name };

    private void NoteUnchangedClassicBinding(Scope scope, string name, ScriptUnit unit, bool requiresWindow)
    {
        var key = (scope, name);
        if (!unchangedClassicBindings.TryGetValue(key, out var bindings)) unchangedClassicBindings[key] = bindings = [];
        bindings.Add((unit, requiresWindow));
    }

    private void Index(Node node, ScriptUnit unit, Scope scope, CancellationToken token, int depth, bool conditional,
        bool canTrackGeneratedScope = true)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 128) { Notice("JavaScriptの入れ子が深いため、一部の宣言とイベントを省略しました。"); return; }
        scopes[node] = scope;
        owners[node] = scope.OwnerEntryId;
        if (RazorBrowserCalls.StandaloneFocus(node) is { } focus) standaloneFocus.Add(focus);
        if (conditional) conditionalNodes.Add(node);
        if (canTrackGeneratedScope && node is EmptyStatement && unit.RenderingSlots?.TryGetValue(node.Start, out var slot) == true)
            generatedStatementScopes[slot.Offset] = scope;
        if (node is IFunction function)
        {
            var functionScope = new Scope(scope);
            var name = function.Id?.Name ?? $"無名関数 L{Range(unit, node).Line}";
            var entry = AddEntry(name, function.Body, unit, functionScope, node);
            functionScope.OwnerEntryId = entry.Entry.Id;
            functions[node] = entry;
            if (node is FunctionDeclaration { Id: { } id })
            {
                scope.Declare(id.Name, conditional ? null : entry.Entry.Id);
                if (!conditional && (scope == globals || isolatedClassicScopes.ContainsKey(scope)))
                {
                    windowFunctions.Add(entry.Entry.Id);
                    classicFunctionDeclarations.Add(entry.Entry.Id);
                    entry.IsTopLevelClassicFunction = depth == 1;
                }
            }
            if (function.Id is { } ownName)
                functionScope.Declare(ownName.Name, entry.Entry.Id);
            foreach (var parameter in function.Params)
                foreach (var identifier in Descendants(parameter).OfType<Identifier>())
                    functionScope.Declare(identifier.Name, null);
            Index(function.Body, unit, functionScope, token, depth + 1, conditional: false, canTrackGeneratedScope);
            return;
        }
        foreach (var child in node.ChildNodes)
            Index(child, unit, scope, token, depth + 1,
                conditional || node is IfStatement or ConditionalExpression or WhileStatement or ForStatement
                    or ForInStatement or ForOfStatement or SwitchCase or TryStatement,
                canTrackGeneratedScope && node is not (WithStatement or CatchClause));

        if (node is AssignmentExpression { Left: MemberExpression changed } && RazorBrowserCalls.CouldOverrideRoutineMethod(changed))
        {
            if (changed.Object is Identifier receiver)
                (scope.DeclarationScope(receiver.Name) ?? globals).BlockDomProof(receiver.Name);
            else if (IsDocument(changed.Object, scope)) globals.BlockDomProof("document");
        }

        if (node is VariableDeclarator { Id: Identifier variable } declarator)
        {
            scope.Declare(variable.Name, declarator.Init is { } initial && functions.TryGetValue(initial, out var initializerFunction)
                && !conditional ? initializerFunction.Entry.Id : null);
            if (declarator.Init is { } functionValue && functions.TryGetValue(functionValue, out var namedFunction)
                && functionValue is IFunction { Id: null })
                RenameEntry(namedFunction, variable.Name);
            if (declarator.Init is { } initialValue && RazorBrowserCalls.IsExplicitFocusReceiver(initialValue, scope))
                scope.RememberExplicitFocusReceiver(variable.Name);
            if (!conditional && declarator.Init is { } value)
            {
                if (Selector(value, scope) is { } selector) scope.Selectors.TryAdd(variable.Name, selector);
                if (RazorBrowserCalls.IsElement(value, scope)) scope.RememberDomElement(variable.Name);
                if (IsJQuery(value, scope)) scope.JQueryVariables.Add(variable.Name);
                if (IsXhrConstructor(value, scope, out var legacy)) scope.Xhrs.TryAdd(variable.Name, new XhrState(legacy));
                else if (value is Identifier alias && scope.FindXhr(alias.Name) is { } client) scope.Xhrs.TryAdd(variable.Name, client);
            }
        }
        else if (node is VariableDeclarator { Id: var pattern })
        {
            foreach (var identifier in JavaScriptWriteTargets.Leaves(pattern).OfType<Identifier>())
                scope.Declare(identifier.Name, null);
        }
        else if (node is AssignmentExpression { Left: ArrayPattern or ObjectPattern } destructuring)
        {
            foreach (var target in JavaScriptWriteTargets.Leaves(destructuring.Left))
            {
                if (target is Identifier id) scope.Block(id.Name);
                else if (target is MemberExpression { Object: Identifier { Name: "window" } } member
                    && !scope.IsDeclared("window") && PropertyName(member) is { } property)
                    (isolatedClassicScopes.ContainsKey(scope) ? scope : globals).Block(property);
            }
        }
        else if (node is UpdateExpression { Argument: Identifier counter })
            scope.Block(counter.Name);
        else if (node is ClassDeclaration { Id: { } declaredClass })
        {
            scope.Declare(declaredClass.Name, null);
            scope.RememberExplicitFocusReceiver(declaredClass.Name);
        }
        else if (node is AssignmentExpression { Left: Identifier assigned } assignment)
        {
            var declarationScope = scope.DeclarationScope(assigned.Name) ?? globals;
            var previousXhr = declarationScope.FindXhr(assigned.Name);
            // 閉包から外側の変数が変更される場合も、元の関数への確定リンクを消す。
            scope.Block(assigned.Name);
            if (RazorBrowserCalls.IsExplicitFocusReceiver(assignment.Right, scope))
                declarationScope.RememberExplicitFocusReceiver(assigned.Name);
            if (IsXhrConstructor(assignment.Right, scope, out var legacy))
            {
                var client = previousXhr ?? new XhrState(legacy);
                client.Legacy |= legacy;
                client.ConditionalConstruction |= conditional || previousXhr is not null;
                declarationScope.Xhrs[assigned.Name] = client;
                // 同じ関数内の代入では、このスコープからも参照できるようにする。
                scope.Xhrs[assigned.Name] = client;
            }
        }
        else if (node is AssignmentExpression { Left: MemberExpression { Object: Identifier { Name: "window" } } property } windowAssignment
            && !scope.IsDeclared("window") && PropertyName(property) is { } name)
        {
            var scriptScope = isolatedClassicScopes.ContainsKey(scope) ? scope : globals;
            var publishes = depth == 2 && !conditional && !unit.ContainsRazor && ReferenceEquals(scope, scriptScope)
                && windowAssignment.Operator == Operator.Assignment;
            if (publishes && functions.TryGetValue(windowAssignment.Right, out var assignedFunction))
            {
                scriptScope.Declare(name, assignedFunction.Entry.Id);
                windowFunctions.Add(assignedFunction.Entry.Id);
                explicitWindowFunctions.Add(assignedFunction.Entry.Id);
                if (windowAssignment.Right is IFunction { Id: null })
                    RenameEntry(assignedFunction, name);
            }
            else
            {
                scriptScope.Block(name);
                // A named function declaration in this script is hoisted; other values may not exist yet.
                if (publishes && windowAssignment.Right is Identifier reference)
                {
                    if (name == reference.Name && unit.Recovery is null)
                        NoteUnchangedClassicBinding(scope, name, unit, requiresWindow: true);
                    else
                        windowAliases.Add((scope, unit, name, reference.Name));
                }
            }
        }
        if (node is CallExpression { Callee: Identifier { Name: "eval" } })
        {
            scope.Dynamic = true;
            globals.Dynamic = true;
            Notice($"L{Range(unit, node).Line}: evalによる名前の変更は解決できません。");
        }
    }

    private void FindBindingsAndRequests(Node root, ScriptUnit unit, CancellationToken token)
    {
        foreach (var node in Descendants(root))
        {
            token.ThrowIfCancellationRequested();
            if (!scopes.TryGetValue(node, out var scope)) continue;
            if (node is CallExpression call)
            {
                FindCallBinding(call, unit, scope);
                FindRequest(call, unit, scope);
            }
            else if (node is AssignmentExpression { Left: MemberExpression member } assignment
                && PropertyName(member) is { } property && property.StartsWith("on", StringComparison.Ordinal)
                && property.Length > 2)
            {
                AddBinding(Selector(member.Object, scope) ?? "動的な要素", property[2..], assignment.Right,
                    assignment, unit, scope, conditional: conditionalNodes.Contains(node) || scope.Parent is not null
                        || unit.ContainsRazor || unit.Recovery is not null,
                    xhr: member.Object is Identifier client ? scope.FindXhr(client.Name) : null);
            }
        }
    }

    private void CompleteCallbacks()
    {
        lock (callbackLock)
        {
            if (!callbacksDirty) return;
            // A handler registered after send still belongs to that client, not the synchronous flow.
            for (var i = 0; i < requests.Count; ++i)
            {
                var ids = requestClients[i].CallbackIds;
                if (!ReferenceEquals(requests[i].CallbackEntryIds, ids) && ids.Count > 0)
                    requests[i] = requests[i] with { CallbackEntryIds = ids };
            }
            callbacksDirty = false;
        }
    }

    private void FindCallBinding(CallExpression call, ScriptUnit unit, Scope scope)
    {
        if (call.Callee is not MemberExpression member || PropertyName(member) is not { } method) return;
        var args = call.Arguments;
        var selector = Selector(member.Object, scope) ?? "動的な要素";
        var nested = scope.Parent is not null || conditionalNodes.Contains(call) || unit.ContainsRazor
            || unit.Recovery is not null;
        if (method is "addEventListener" or "attachEvent" && args.Count >= 2)
        {
            var eventName = StringValue(args[0]);
            if (eventName is null) { Notice($"L{Range(unit, call).Line}: 動的なイベント名は解決できません。"); return; }
            if (method == "attachEvent" && eventName.StartsWith("on", StringComparison.Ordinal)) eventName = eventName[2..];
            AddBinding(selector, eventName, args[1], call, unit, scope, conditional: nested, legacy: method == "attachEvent",
                xhr: member.Object is Identifier client ? scope.FindXhr(client.Name) : null);
            return;
        }
        if (!IsJQuery(member.Object, scope)) return;
        if (method is "on" or "bind" or "live" && args.Count >= 2)
        {
            if (StringValue(args[0]) is not { } events) return;
            var delegated = method == "live" || method == "on" && args.Count >= 3 && StringValue(args[1]) is not null;
            if (method == "on" && delegated) selector = StringValue(args[1])!;
            foreach (var eventName in events.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                AddBinding(selector, eventName.Split('.')[0], args[^1], call, unit, scope, delegated, nested,
                    legacy: method == "live");
        }
        else if (method == "delegate" && args.Count >= 3 && StringValue(args[0]) is { } delegatedSelector
            && StringValue(args[1]) is { } events)
        {
            foreach (var eventName in events.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                AddBinding(delegatedSelector, eventName.Split('.')[0], args[^1], call, unit, scope,
                    delegated: true, conditional: nested);
        }
        else if (args.Count == 1 && method is "change" or "click" or "submit" or "keyup" or "keydown" or "blur" or "focus")
            AddBinding(selector, method, args[0], call, unit, scope, conditional: nested);
    }

    private void AddBinding(string selector, string eventName, Expression handler, Node registration,
        ScriptUnit unit, Scope scope, bool delegated = false, bool conditional = false, bool legacy = false, XhrState? xhr = null)
    {
        var target = functions.TryGetValue(handler, out var function) ? function.Entry.Id
            : ResolveFunctionReference(handler, scope);
        var name = target is { } id ? data[id].Entry.Name : Slice(unit, handler);
        var binding = new RazorJavaScriptBinding(selector, eventName, target, Range(unit, registration), name, delegated, conditional, legacy);
        bindings.Add(binding);
        if (xhr is not null && target is int handlerId && xhr.RegisterCallback(handlerId))
            callbacksDirty = true;
        if (target is null) Notice($"L{Range(unit, registration).Line}: {eventName}の関数を一意に特定できません。");
        if (legacy) Notice($"L{Range(unit, registration).Line}: {eventName}の旧式イベント登録は実行環境やライブラリの対応確認が必要です。");
        if (selector == "動的な要素") Notice($"L{Range(unit, registration).Line}: イベント対象の要素を静的に特定できません。");
    }

    private void FindRequest(CallExpression call, ScriptUnit unit, Scope scope)
    {
        if (call.Callee is not MemberExpression { Object: Identifier variable } member || PropertyName(member) is not { } method
            || scope.FindXhr(variable.Name) is not { } xhr) return;
        var ownerId = owners.GetValueOrDefault(call, scope.OwnerEntryId);
        if (!xhrUsages.TryGetValue((xhr, ownerId), out var usage)) xhrUsages[(xhr, ownerId)] = usage = new XhrUsage();
        if (method == "open")
        {
            usage.Method = call.Arguments.Count > 0 ? StringValue(call.Arguments[0]) : null;
            usage.Url = call.Arguments.Count > 1 ? StringValue(call.Arguments[1]) : null;
            usage.UrlExpression = call.Arguments.Count > 1 ? Slice(unit, call.Arguments[1]) : "未指定";
            usage.IsAsync = !(call.Arguments.Count > 2 && call.Arguments[2] is BooleanLiteral { Value: false });
            usage.OpenScope = scope;
            usage.OpenConditional = conditionalNodes.Contains(call);
            ++usage.OpenCount;
            if (call.Arguments.Count > 2 && call.Arguments[2] is not BooleanLiteral)
            {
                usage.AsyncKnown = false;
                Notice($"L{Range(unit, call).Line}: XMLHttpRequestの非同期指定が動的です。");
            }
        }
        else if (method == "send")
        {
            var resolved = usage.Method is not null && usage.Url is not null && ReferenceEquals(usage.OpenScope, scope)
                && !usage.OpenConditional && usage.OpenCount == 1 && usage.AsyncKnown && !unit.ContainsRazor
                && unit.Recovery is null && !xhr.ConditionalConstruction;
            var client = "XMLHttpRequest: " + variable.Name;
            var request = new RazorJavaScriptRequest(owners.GetValueOrDefault(call, scope.OwnerEntryId), client, usage.Method?.ToUpperInvariant() ?? "動的なHTTPメソッド",
                usage.Url ?? usage.UrlExpression ?? "送信先未解決", call.Arguments.Count > 0 ? Slice(unit, call.Arguments[0]) : null,
                Range(unit, call), usage.IsAsync, resolved, []);
            requestNodes[call] = requests.Count;
            requests.Add(request);
            requestClients.Add(xhr);
            callbacksDirty = true;
            if (!resolved) Notice($"L{request.Source.Line}: XMLHttpRequestのメソッドまたはURLを確定できません。");
            if (xhr.Legacy) Notice($"L{request.Source.Line}: ActiveXObjectを使うHTTP送信は旧IE向けの候補です。");
        }
    }

    internal static bool IsXhrConstructor(Expression node, Scope scope, out bool legacy)
    {
        legacy = false;
        if (node is not NewExpression { Callee: Identifier identifier } creation || scope.IsDeclared(identifier.Name)) return false;
        if (identifier.Name == "XMLHttpRequest") return true;
        legacy = identifier.Name == "ActiveXObject" && creation.Arguments.Count > 0
            && StringValue(creation.Arguments[0]) is { } name && name.Contains("XMLHTTP", StringComparison.OrdinalIgnoreCase);
        return legacy;
    }

    private static string? Selector(Expression expression, Scope scope, int depth = 0)
    {
        if (depth >= 16) return null;
        if (expression is Identifier identifier)
            return scope.FindXhr(identifier.Name) is not null ? "XMLHttpRequest: " + identifier.Name : scope.FindSelector(identifier.Name);
        if (expression is CallExpression call)
        {
            if (call.Callee is Identifier { Name: "$" or "jQuery" } jquery && !scope.IsDeclared(jquery.Name) && call.Arguments.Count > 0)
                return StringValue(call.Arguments[0]);
            if (JQueryChainReceiver(call) is { } jqueryReceiver)
                return Selector(jqueryReceiver, scope, depth + 1);
            if (call.Callee is MemberExpression member && IsDocument(member.Object, scope)
                && call.Arguments.Count > 0 && StringValue(call.Arguments[0]) is { } name)
            {
                var property = PropertyName(member);
                if (property == "getElementById") return "#" + name;
                if (property is "querySelector" or "querySelectorAll") return name;
            }
        }
        if (expression is MemberExpression { Computed: true, Object: MemberExpression all } indexed
            && PropertyName(all) == "all" && IsDocument(all.Object, scope) && StringValue(indexed.Property) is { } elementId)
            return "#" + elementId;
        return null;
    }

    internal static bool IsDocument(Expression expression, Scope scope) =>
        expression is Identifier { Name: "document" } && !scope.IsDeclared("document")
        || expression is MemberExpression { Object: Identifier { Name: "window" } } member
            && !scope.IsDeclared("window") && PropertyName(member) == "document";

    internal static bool IsJQuery(Expression expression, Scope scope, RazorGeneratedBindings? generated = null, int depth = 0)
    {
        if (depth >= 16) return false;
        if (expression is Identifier id)
        {
            if (generated?.InitializerAt(id) is { } initializer)
                return IsJQuery(initializer, scope, generated, depth + 1);
            return generated?.IsReceiverUnchanged(id.Name) != false && scope.FindJQuery(id.Name);
        }
        if (expression is not CallExpression call) return false;
        if (call.Callee is Identifier { Name: "$" or "jQuery" } jquery)
            return generated?.IsUnchanged(jquery.Name) != false && !scope.IsDeclared(jquery.Name);
        return JQueryChainReceiver(call) is { } receiver && IsJQuery(receiver, scope, generated, depth + 1);
    }

    private static Expression? JQueryChainReceiver(CallExpression call)
    {
        if (call.Callee is not MemberExpression member) return null;
        return PropertyName(member) switch
        {
            "off" or "removeAttr" or "on" or "bind" or "delegate" or "live" => member.Object,
            "attr" when call.Arguments.Count >= 2 || call.Arguments is [ObjectExpression] => member.Object,
            _ => null
        };
    }

    internal readonly record struct CallProjection(int? Target, bool Omit, EntryData? ReadyCallback = null);

    internal CallProjection ProjectCall(CallExpression call, EntryData entry, bool omitBrowserDetails)
    {
        var scope = scopes.GetValueOrDefault(call, entry.Scope);
        if (call is { Optional: false, Callee: Identifier { Name: "$" or "jQuery" } jquery, Arguments: [IFunction] }
            && entry.Unit.Recovery is null && !scope.HasDynamicBinding && IsJQuery(call, scope)
            && isolatedClassicScopes.Keys.All(other => !other.Dynamic && !other.DeclaresHere(jquery.Name))
            && functions.TryGetValue(call.Arguments[0], out var ready))
            return new(ready.Entry.Id, false, ready);
        var target = ResolveFunctionReference(call.Callee, scope);
        return new(target, omitBrowserDetails && target is null
            && RazorBrowserCalls.IsRoutine(call, scope, isStandaloneFocus: standaloneFocus.Contains(call)));
    }

    internal bool IsHiddenXhrHandler(MemberExpression member, EntryData entry) =>
        RazorBrowserCalls.IsReadyStateHandler(member, scopes.GetValueOrDefault(member, entry.Scope));

    private int? ResolveFunctionReference(Expression reference, Scope scope)
    {
        if (reference is Identifier id) return ResolveCallName(scope, id.Name);
        if (reference is not MemberExpression { Object: Identifier owner } member || PropertyName(member) is not { } name)
            return null;
        if (scope.FindNamespace(owner.Name) is { } module) return module.GetValueOrDefault(name);
        if (owner.Name != "window" || scope.IsDeclared("window"))
            return null;
        var target = scope.IsInInlineEvent ? ResolveCallName(scope, name)
            : scope.Resolve(name) == globals.Resolve(name) ? globals.Resolve(name) : null;
        return target is int key && windowFunctions.Contains(key) ? key : null;
    }

    private int? ResolveCallName(Scope scope, string name)
    {
        var direct = scope.Resolve(name);
        if (direct is int directId && scope.DeclarationScope(name) is { } declaring
            && declaring.IsDeclared("window")
            && (explicitWindowFunctions.Contains(directId) || declaring.WindowAliases.Contains(name))) return null;
        if (!scope.IsInInlineEvent || isolatedClassicScopes.Count == 0) return direct;
        for (var lexical = scope; lexical != globals; lexical = lexical.Parent!)
        {
            if (lexical.Dynamic || lexical.DeclaresHere(name)) return direct;
            if (lexical.Parent is null) return null;
        }
        if (globals.Dynamic) return null;

        Scope? candidate = null;
        foreach (var (isolated, visible) in isolatedClassicScopes)
        {
            if (isolated.Dynamic) return null;
            if (!isolated.DeclaresHere(name)) continue;
            if (globals.DeclaresHere(name) || candidate is not null || !visible) return null;
            candidate = isolated;
        }
        if (candidate is null) return direct;
        var target = candidate.ResolveLocal(name);
        return target is int key && explicitWindowFunctions.Contains(key) && candidate.IsDeclared("window") ? null : target;
    }

    internal MethodEntry? GetEntry(int id) => data.GetValueOrDefault(id)?.Entry;
    internal bool HasSourceOnlyDefinitions => sourceOnlyDefinitions.Count > 0;

    internal record CallSite(string? Name, SourceRange Source, MethodEntry Owner, FlowTarget? Target,
        SourceRange? SourceOnlyDefinition = null);

    internal IEnumerable<CallSite> CallSites(int? ownerId, CancellationToken token, bool omitRoutine = false)
    {
        if (ownerId is int selected)
        {
            if (data.TryGetValue(selected, out var entry) && entry.Body is { } body)
                foreach (var call in Descendants(body).OfType<CallExpression>())
                {
                    token.ThrowIfCancellationRequested();
                    if (owners.TryGetValue(call, out var owner) && owner == selected
                        && DescribeCall(call, entry, omitRoutine) is { } site) yield return site;
                }
            yield break;
        }

        foreach (var (node, owner) in owners)
        {
            token.ThrowIfCancellationRequested();
            if (node is CallExpression call && owner is int key && data.TryGetValue(key, out var entry)
                && DescribeCall(call, entry, omitRoutine) is { } site) yield return site;
        }
    }

    private CallSite? DescribeCall(CallExpression call, EntryData entry, bool omitRoutine)
    {
        var projection = ProjectCall(call, entry, omitRoutine);
        if (projection.Omit) return null;
        var name = call.Callee switch
        {
            Identifier identifier => identifier.Name,
            MemberExpression { Object: Identifier receiver } member when PropertyName(member) is { } property
                => receiver.Name + "." + property,
            _ => null
        };
        var id = projection.Target;
        var target = ResolveNavigationTarget(id);
        SourceRange? candidate = null;
        if (id is null && call.Callee is Identifier bare && sourceOnlyDefinitions.TryGetValue(bare.Name, out var found)
            && found is not null && scopes.TryGetValue(call, out var scope) && scope.IsInInlineEvent
            && !scope.HasDynamicBinding && !scope.IsDeclared(bare.Name)
            && isolatedClassicScopes.Keys.All(isolated => !isolated.DeclaresHere(bare.Name) && !isolated.Dynamic))
            candidate = found;
        return new CallSite(name, Range(entry.Unit, call), entry.Entry,
            target is (int method, string path) && id is int key
                ? new FlowTarget(data[key].Entry.Signature, method, path) : null, candidate);
    }

    internal RazorJavaScriptRequest? Request(Node node)
    {
        if (!requestNodes.TryGetValue(node, out var index)) return null;
        CompleteCallbacks();
        return requests[index];
    }
    internal void Notice(string message)
    {
        var notice = "解析は部分的 · " + message;
        if (!notices.Contains(notice)) notices.Add(notice);
    }

    internal SourceRange Range(ScriptUnit unit, Node node)
    {
        var start = EncodedOffset(unit, node.Start);
        return new SourceRange(unit.Source.Offset + start, Math.Max(0, MappedEnd(unit, node) - start), unit.LineAt(start),
            unit.Source.FilePath ?? pagePath);
    }

    internal static string Slice(ScriptUnit unit, Node node, Node? last = null)
    {
        var start = EncodedOffset(unit, node.Start);
        var end = Math.Clamp(MappedEnd(unit, last ?? node), start, unit.OriginalText.Length);
        var value = unit.OriginalText[start..end].Trim();
        return value.Length > 140 ? value[..139] + "…" : value;
    }

    // Razor式を同じ長さの「0 + 空白」に射影すると、JS ASTの終端は0の直後になる。
    // 終端トークンが置換された場合だけ、その後の置換空白を元の式の末尾まで戻す。
    // 通常のJSの末尾から、別のRazor制御ブロックへ範囲を伸ばすことはしない。
    private static int MappedEnd(ScriptUnit unit, Node node)
    {
        var end = ParsedToProjectedOffset(unit, node.End);
        var encodedEnd = unit.InlineText?.EncodedOffset(end) ?? end;
        if (!unit.ContainsRazor || end == 0) return encodedEnd;
        var tokenStart = end;
        var start = ParsedToProjectedOffset(unit, node.Start);
        while (tokenStart > start && (char.IsLetterOrDigit(unit.Text[tokenStart - 1])
            || unit.Text[tokenStart - 1] is '_' or '$')) --tokenStart;
        var encodedStart = unit.InlineText?.EncodedOffset(tokenStart) ?? tokenStart;
        var projected = unit.InlineText?.Encoded ?? unit.Text;
        var length = encodedEnd - encodedStart;
        if (length == 0 || projected.AsSpan(encodedStart, length)
            .SequenceEqual(unit.OriginalText.AsSpan(encodedStart, length))) return encodedEnd;
        var mappedEnd = encodedEnd;
        for (var cursor = encodedEnd; cursor < projected.Length && char.IsWhiteSpace(projected[cursor]); ++cursor)
            if (projected[cursor] != unit.OriginalText[cursor]) mappedEnd = cursor + 1;
        return mappedEnd;
    }

    private static int EncodedOffset(ScriptUnit unit, int offset)
    {
        offset = ParsedToProjectedOffset(unit, offset);
        return unit.InlineText?.EncodedOffset(offset) ?? offset;
    }

    private static int ParsedToProjectedOffset(ScriptUnit unit, int offset)
    {
        var recovery = unit.Recovery;
        offset = Math.Clamp(offset, 0, unit.Text.Length + (recovery?.ParsedLengthDelta ?? 0));
        return recovery?.SourceOffset(offset) ?? offset;
    }

    internal static string? PropertyName(MemberExpression member) => member.Computed
        ? StringValue(member.Property) : (member.Property as Identifier)?.Name;
    private static string? StringValue(Expression expression) => (expression as StringLiteral)?.Value;

    internal static IEnumerable<Node> Descendants(Node root)
    {
        yield return root;
        var pending = new Stack<ChildNodes.Enumerator>();
        var children = root.ChildNodes.GetEnumerator();
        while (true)
        {
            if (!children.MoveNext())
            {
                if (!pending.TryPop(out children)) yield break;
                continue;
            }
            var node = children.Current;
            yield return node;
            pending.Push(children);
            children = node.ChildNodes.GetEnumerator();
        }
    }

    internal sealed record ScriptUnit(string Text, string OriginalText, SourceRange Source, JsProgram Root,
        bool ContainsRazor, IReadOnlyDictionary<int, SourceRange>? RenderingSlots = null,
        InlineScriptText? InlineText = null, JavaScriptRecovery? Recovery = null)
    {
        private readonly int[]? lineStarts = OriginalText.Length < 4096 ? null : ReadLineStarts(OriginalText);

        public int LineAt(int offset)
        {
            if (lineStarts is null) return Source.Line + OriginalText.AsSpan(0, offset).Count('\n');
            var index = Array.BinarySearch(lineStarts, offset);
            return Source.Line + (index < 0 ? ~index : index + 1);
        }

        private static int[] ReadLineStarts(string text)
        {
            var starts = new int[text.AsSpan().Count('\n')];
            var start = 0;
            for (var i = 0; i < starts.Length; i++)
                starts[i] = start = text.IndexOf('\n', start) + 1;
            return starts;
        }
    }
    internal sealed class EntryData(MethodEntry entry, Node? body, ScriptUnit unit, Scope scope, int index = -1)
    {
        public int Index { get; } = index;
        public MethodEntry Entry { get; set; } = entry;
        public Node? Body { get; } = body;
        public ScriptUnit Unit { get; } = unit;
        public Scope Scope { get; } = scope;
        public string? Failure { get; set; }
        public bool IsTopLevelClassicFunction { get; set; }
    }
    internal sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, List<int?>> names = new(StringComparer.Ordinal);
        private HashSet<string>? domElements;
        private HashSet<string>? domOverrides;
        private HashSet<string>? explicitFocusReceivers;
        public Scope? Parent { get; } = parent;
        public bool InlineEvent { get; init; }
        public bool IsInInlineEvent => InlineEvent || Parent?.IsInInlineEvent == true;
        public bool DeclaresHere(string name) => names.ContainsKey(name);
        public int OwnerEntryId { get; set; }
        public bool Dynamic { get; set; }
        public bool HasDynamicBinding => Dynamic || Parent?.HasDynamicBinding == true;
        public Dictionary<string, Dictionary<string, int?>> Namespaces { get; } = new(StringComparer.Ordinal);
        public HashSet<string> WindowAliases { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Selectors { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, XhrState> Xhrs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> JQueryVariables { get; } = new(StringComparer.Ordinal);
        public void Declare(string name, int? entry)
        {
            if (!names.TryGetValue(name, out var entries)) names[name] = entries = [];
            entries.Add(entry);
        }
        public void Block(string name)
        {
            Declare(name, null);
            Namespaces.Remove(name);
            Selectors.Remove(name);
            Xhrs.Remove(name);
            JQueryVariables.Remove(name);
            // 宣言収集前のvarもあるため、見えている外側の同名変数は保守的に無効化する。
            if (Parent?.IsDeclared(name) == true) Parent.Block(name);
        }
        public void PublishWindowAlias(string name, int target)
        {
            if (!names.TryGetValue(name, out var declared) || declared is not [null]) return;
            declared[0] = target;
            WindowAliases.Add(name);
        }
        public void KeepUniqueDeclaration(string name, Func<int, int> unchangedCount)
        {
            if (Dynamic || !names.TryGetValue(name, out var declared) || declared.Count < 2) return;
            int? target = null;
            foreach (var candidate in declared)
            {
                if (candidate is null) continue;
                if (target is not null) return;
                target = candidate;
            }
            if (target is { } id && unchangedCount(id) == declared.Count - 1) names[name] = [id];
        }
        public Scope? DeclarationScope(string name) => names.ContainsKey(name) ? this : Parent?.DeclarationScope(name);
        public bool IsDeclared(string name) => names.ContainsKey(name) || Parent?.IsDeclared(name) == true;
        public void RememberDomElement(string name) => (domElements ??= new(StringComparer.Ordinal)).Add(name);
        public void BlockDomProof(string name) => (domOverrides ??= new(StringComparer.Ordinal)).Add(name);
        public bool HasDomOverride(string name) => domOverrides?.Contains(name) == true || Parent?.HasDomOverride(name) == true;
        public void RememberExplicitFocusReceiver(string name) => (explicitFocusReceivers ??= new(StringComparer.Ordinal)).Add(name);
        public bool IsExplicitFocusReceiver(string name) => explicitFocusReceivers?.Contains(name) == true
            || !names.ContainsKey(name) && Parent?.IsExplicitFocusReceiver(name) == true;
        public bool IsDomElement(string name) => names.TryGetValue(name, out var declared)
            ? declared.Count == 1 && domElements?.Contains(name) == true && domOverrides?.Contains(name) != true
            : Parent?.IsDomElement(name) == true;
        public bool FindJQuery(string name) => names.TryGetValue(name, out var declared)
            ? declared.Count == 1 && JQueryVariables.Contains(name) && domOverrides?.Contains(name) != true
            : Parent?.FindJQuery(name) == true;
        public int? ResolveLocal(string name) => !Dynamic && names.TryGetValue(name, out var declared)
            && declared.Count == 1 ? declared[0] : null;
        public IReadOnlyDictionary<string, int?>? FindNamespace(string name) => Dynamic ? null
            : names.TryGetValue(name, out var declared) ? declared.Count == 1 ? Namespaces.GetValueOrDefault(name) : null
            : Parent?.FindNamespace(name);
        public int? Resolve(string name) => Dynamic ? null : names.TryGetValue(name, out var candidates)
            ? candidates.Count == 1 ? candidates[0] : null : Parent?.Resolve(name);
        public string? FindSelector(string name) => names.ContainsKey(name) ? Selectors.GetValueOrDefault(name) : Parent?.FindSelector(name);
        public XhrState? FindXhr(string name) => names.ContainsKey(name) ? Xhrs.GetValueOrDefault(name) : Parent?.FindXhr(name);
    }
    internal sealed class XhrState(bool legacy)
    {
        private List<int>? callbackOrder;
        private HashSet<int>? callbacks;
        private IReadOnlyList<int>? snapshot;

        public IReadOnlyList<int> CallbackIds => snapshot ??= callbackOrder?.ToArray() ?? [];

        public bool RegisterCallback(int id)
        {
            if (!(callbacks ??= []).Add(id)) return false;
            (callbackOrder ??= []).Add(id);
            snapshot = null;
            return true;
        }

        public bool Legacy { get; set; } = legacy;
        public bool ConditionalConstruction { get; set; }
    }
    private sealed class XhrUsage
    {
        public string? Method { get; set; }
        public string? Url { get; set; }
        public string? UrlExpression { get; set; }
        public bool IsAsync { get; set; } = true;
        public Scope? OpenScope { get; set; }
        public bool OpenConditional { get; set; }
        public int OpenCount { get; set; }
        public bool AsyncKnown { get; set; } = true;
    }
}
