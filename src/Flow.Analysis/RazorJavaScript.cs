using System.Net;
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
    internal bool IsolatedClassic { get; init; }
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
    private readonly List<MethodEntry> entries = [];
    private readonly List<string> notices = [];
    private readonly List<RazorJavaScriptBinding> bindings = [];
    private readonly List<RazorJavaScriptRequest> requests = [];
    private readonly Dictionary<int, EntryData> data = [];
    private readonly Dictionary<Node, EntryData> functions = [];
    private readonly Dictionary<Node, Scope> scopes = [];
    private readonly Dictionary<Node, int> owners = [];
    private readonly HashSet<Node> conditionalNodes = [];
    private readonly Dictionary<Node, RazorJavaScriptRequest> requestNodes = [];
    private readonly Dictionary<RazorJavaScriptBinding, XhrState> bindingXhrs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RazorJavaScriptRequest, XhrState> requestXhrs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(XhrState Client, int Owner), XhrUsage> xhrUsages = [];
    private readonly Scope globals = new(null);
    private readonly HashSet<int> windowFunctions = [];
    private readonly Dictionary<string, ScriptUnit> externalUnits = new(LocalJavaScriptFiles.PathComparer);
    private readonly Dictionary<string, (ScriptUnit Unit, Scope Scope)> modules = new(LocalJavaScriptFiles.PathComparer);
    private int nextId = -2;

    public IReadOnlyList<MethodEntry> Entries => entries;
    public IReadOnlyList<string> Notices => notices;
    public IReadOnlyList<RazorJavaScriptBinding> Bindings => bindings;
    public IReadOnlyList<RazorJavaScriptRequest> Requests => requests;

    public RazorJavaScript(string pageText, string pagePath, IEnumerable<RazorJavaScriptFragment> fragments,
        CancellationToken token = default) : this(pageText, pagePath, fragments, null, token) { }

    internal RazorJavaScript(string pageText, string pagePath, IEnumerable<RazorJavaScriptFragment> fragments,
        Func<string, string, RazorJavaScriptFragment?>? loadImport, CancellationToken token,
        bool standalone = false)
    {
        this.pageText = pageText;
        this.pagePath = pagePath;
        var parsed = new List<(ScriptUnit Unit, Scope Scope)>();
        foreach (var fragment in fragments) IndexFragment(fragment);
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
            var unit = Parse(fragment, inline: false);
            if (unit is null && standalone && LocalJavaScriptFiles.PathComparer.Equals(path, pagePath) && !fragment.IsModule)
            {
                var classicFailure = notices.Skip(diagnosticsBeforeParse).ToArray();
                notices.RemoveRange(diagnosticsBeforeParse, notices.Count - diagnosticsBeforeParse);
                var module = Parse(fragment with { IsModule = true }, inline: false);
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
                    var failedUnit = new ScriptUnit(fragment.Text, fragment.Text, fragment.Source, null!, false,
                        fragment.ContainsRazorCode, fragment.RenderingSlots);
                    var failed = AddEntry(name, null, failedUnit, scope);
                    failed.Failure = external ? "JavaScriptの構文を確定できません。"
                        : "Razorによる生成部分を除いたJavaScriptは構文を確定できません。";
                    if (external) externalUnits.Add(path!, failedUnit);
                }
                return;
            }

            if (external) externalUnits.Add(path!, unit);
            if (external && fragment.IsModule) modules.Add(path!, (unit, scope));
            var entry = AddEntry(name, unit.Root, unit, scope);
            scope.OwnerEntryId = entry.Entry.Id;
            Index(unit.Root, unit, scope, token, 0, conditional: false);
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
            }
        }
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
    public int AddInlineHandler(string name, string code, SourceRange source, bool containsRazorCode = false)
    {
        var decoded = WebUtility.HtmlDecode(code);
        var fragment = new RazorJavaScriptFragment(decoded, source, containsRazorCode);
        var unit = Parse(fragment, inline: true, approximate: decoded.Length != code.Length);
        if (unit is null)
        {
            var failed = AddEntry(name, null, new ScriptUnit(decoded, decoded, source, null!, true, containsRazorCode), new Scope(globals));
            failed.Failure = "JavaScriptを静的に解析できません。診断を確認してください。";
            return failed.Entry.Id;
        }
        var scope = new Scope(globals);
        var entry = AddEntry(name, unit.Root, unit, scope);
        scope.OwnerEntryId = entry.Entry.Id;
        // インラインイベントのeventはブラウザーが渡す引数として扱う。
        scope.Declare("event", null);
        Index(unit.Root, unit, scope, CancellationToken.None, 0, conditional: false);
        FindBindingsAndRequests(unit.Root, unit, CancellationToken.None);
        return entry.Entry.Id;
    }

    /// <summary>選択されたJS関数だけの制御フローを作り、大きなページの全展開を避ける。</summary>
    public FlowDiagram Analyze(int entryId, CancellationToken token = default)
    {
        if (!data.TryGetValue(entryId, out var entry))
            throw new ArgumentOutOfRangeException(nameof(entryId));
        return new RazorJavaScriptProjector(this, entry, token).Build();
    }

    internal (SourceRange Range, IReadOnlyList<SourceRange> NestedFunctions)? ScriptRenderingScope(int entryId)
    {
        var entry = data[entryId];
        if (entry.Unit.RenderingSlots is not { Count: > 0 }) return null;
        var range = entry.Body is JsProgram or null ? entry.Unit.Source : entry.Entry.Source;
        var nested = entry.Body is null ? [] : Descendants(entry.Body)
            .Where(node => node is IFunction).Select(node => Range(entry.Unit, node)).ToArray();
        return (range, nested);
    }

    private ScriptUnit? Parse(RazorJavaScriptFragment fragment, bool inline, bool approximate = false)
    {
        if (fragment.ContainsRazorCode)
        {
            Notice($"L{fragment.Source.Line}: Razorを含むJavaScriptは生成結果と動的な値を確定できません。抽出した処理は候補です。");
        }
        try
        {
            var parser = new Parser(new ParserOptions { AllowReturnOutsideFunction = inline });
            JsProgram root = fragment.IsModule ? parser.ParseModule(fragment.Text) : parser.ParseScript(fragment.Text);
            if (fragment.RenderingSlots is { Count: > 0 } slots)
            {
                var statementSlots = Descendants(root).OfType<EmptyStatement>().Select(node => node.Start).ToHashSet();
                if (slots.Keys.Any(offset => !statementSlots.Contains(offset)))
                {
                    Notice($"L{fragment.Source.Line}: Razor生成部分の境界を独立したJavaScript文として確定できません。");
                    return null;
                }
            }
            var original = !approximate && fragment.Source.Offset >= 0 && fragment.Source.Offset + fragment.Text.Length <= pageText.Length
                && (fragment.Source.FilePath is null || fragment.Source.FilePath == pagePath)
                ? pageText.Substring(fragment.Source.Offset, fragment.Text.Length) : fragment.Text;
            return new ScriptUnit(fragment.Text, original, fragment.Source, root, approximate, fragment.ContainsRazorCode,
                fragment.RenderingSlots);
        }
        catch (ParseErrorException error)
        {
            Notice($"L{fragment.Source.Line + Math.Max(0, error.LineNumber - 1)}: JavaScript構文エラー ({error.Error.Code})。");
            return null;
        }
        catch (InsufficientExecutionStackException)
        {
            Notice($"L{fragment.Source.Line}: JavaScriptの入れ子が深いため解析できません。");
            return null;
        }
    }

    private EntryData AddEntry(string name, Node? body, ScriptUnit unit, Scope scope)
    {
        var source = body is JsProgram or null ? unit.Source : Range(unit, body);
        var entry = new MethodEntry(nextId--, name, name, source);
        entries.Add(entry);
        var detail = new EntryData(entry, body, unit, scope);
        data.Add(entry.Id, detail);
        return detail;
    }

    private void Index(Node node, ScriptUnit unit, Scope scope, CancellationToken token, int depth, bool conditional)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 128) { Notice("JavaScriptの入れ子が深いため、一部の宣言とイベントを省略しました。"); return; }
        scopes[node] = scope;
        owners[node] = scope.OwnerEntryId;
        if (conditional) conditionalNodes.Add(node);
        if (node is IFunction function)
        {
            var functionScope = new Scope(scope);
            var name = function.Id?.Name ?? $"無名関数 L{Range(unit, node).Line}";
            var entry = AddEntry(name, function.Body, unit, functionScope);
            functionScope.OwnerEntryId = entry.Entry.Id;
            functions[node] = entry;
            if (node is FunctionDeclaration { Id: { } id })
            {
                scope.Declare(id.Name, conditional ? null : entry.Entry.Id);
                if (!conditional && ReferenceEquals(scope, globals)) windowFunctions.Add(entry.Entry.Id);
            }
            if (function.Id is { } ownName)
                functionScope.Declare(ownName.Name, entry.Entry.Id);
            foreach (var parameter in function.Params)
                foreach (var identifier in Descendants(parameter).OfType<Identifier>())
                    functionScope.Declare(identifier.Name, null);
            Index(function.Body, unit, functionScope, token, depth + 1, conditional: false);
            return;
        }
        foreach (var child in node.ChildNodes)
            Index(child, unit, scope, token, depth + 1,
                conditional || node is IfStatement or ConditionalExpression or WhileStatement or ForStatement
                    or ForInStatement or ForOfStatement or SwitchCase or TryStatement);

        if (node is VariableDeclarator { Id: Identifier variable } declarator)
        {
            scope.Declare(variable.Name, declarator.Init is { } initial && functions.TryGetValue(initial, out var initializerFunction)
                && !conditional ? initializerFunction.Entry.Id : null);
            if (declarator.Init is { } functionValue && functions.TryGetValue(functionValue, out var namedFunction)
                && functionValue is IFunction { Id: null })
            {
                var renamed = namedFunction.Entry with { Name = variable.Name, Signature = variable.Name };
                entries[entries.FindIndex(item => item.Id == renamed.Id)] = renamed;
                namedFunction.Entry = renamed;
            }
            if (!conditional && declarator.Init is { } value)
            {
                if (Selector(value, scope) is { } selector) scope.Selectors.TryAdd(variable.Name, selector);
                if (IsJQuery(value, scope)) scope.JQueryVariables.Add(variable.Name);
                if (IsXhrConstructor(value, scope, out var legacy)) scope.Xhrs.TryAdd(variable.Name, new XhrState(legacy));
            }
        }
        else if (node is VariableDeclarator { Id: var pattern })
        {
            foreach (var identifier in Descendants(pattern).OfType<Identifier>())
                scope.Declare(identifier.Name, null);
        }
        else if (node is ClassDeclaration { Id: { } declaredClass })
            scope.Declare(declaredClass.Name, null);
        else if (node is AssignmentExpression { Left: Identifier assigned } assignment)
        {
            var declarationScope = scope.DeclarationScope(assigned.Name) ?? globals;
            var previousXhr = declarationScope.FindXhr(assigned.Name);
            // 閉包から外側の変数が変更される場合も、元の関数への確定リンクを消す。
            scope.Block(assigned.Name);
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
        else if (node is AssignmentExpression { Left: MemberExpression { Object: Identifier { Name: "window" } } property }
            && !scope.IsDeclared("window") && PropertyName(property) is { } name)
        {
            globals.Block(name);
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
                    assignment, unit, scope, conditional: conditionalNodes.Contains(node) || scope.Parent is not null || unit.ContainsRazor,
                    xhr: member.Object is Identifier client ? scope.FindXhr(client.Name) : null);
            }
        }
        // sendより後のコールバック代入も候補として表示する。呼び出し順の保証とは区別する。
        for (var i = 0; i < requests.Count; ++i)
        {
            var request = requests[i];
            if (!requestXhrs.TryGetValue(request, out var client)) continue;
            var callbackIds = bindings.Where(binding => bindingXhrs.TryGetValue(binding, out var boundClient)
                    && ReferenceEquals(client, boundClient) && binding.HandlerEntryId is not null)
                .Select(binding => binding.HandlerEntryId!.Value).Distinct().ToArray();
            if (callbackIds.Length > 0)
            {
                var updated = request with { CallbackEntryIds = callbackIds };
                requests[i] = updated;
                requestXhrs.Remove(request);
                requestXhrs[updated] = client;
                foreach (var key in requestNodes.Where(pair => ReferenceEquals(pair.Value, request)).Select(pair => pair.Key).ToArray())
                    requestNodes[key] = updated;
            }
        }
    }

    private void FindCallBinding(CallExpression call, ScriptUnit unit, Scope scope)
    {
        if (call.Callee is not MemberExpression member || PropertyName(member) is not { } method) return;
        var args = call.Arguments;
        var selector = Selector(member.Object, scope) ?? "動的な要素";
        var nested = scope.Parent is not null || conditionalNodes.Contains(call) || unit.ContainsRazor;
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
            : handler is Identifier identifier ? scope.Resolve(identifier.Name) : null;
        var name = target is { } id ? data[id].Entry.Name : Slice(unit, handler);
        var binding = new RazorJavaScriptBinding(selector, eventName, target, Range(unit, registration), name, delegated, conditional, legacy);
        bindings.Add(binding);
        if (xhr is not null) bindingXhrs[binding] = xhr;
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
                && !usage.OpenConditional && usage.OpenCount == 1 && usage.AsyncKnown && !unit.ContainsRazor && !xhr.ConditionalConstruction;
            var client = "XMLHttpRequest: " + variable.Name;
            var request = new RazorJavaScriptRequest(owners.GetValueOrDefault(call, scope.OwnerEntryId), client, usage.Method?.ToUpperInvariant() ?? "動的なHTTPメソッド",
                usage.Url ?? usage.UrlExpression ?? "送信先未解決", call.Arguments.Count > 0 ? Slice(unit, call.Arguments[0]) : null,
                Range(unit, call), usage.IsAsync, resolved, []);
            requests.Add(request);
            requestNodes[call] = request;
            requestXhrs[request] = xhr;
            if (!resolved) Notice($"L{request.Source.Line}: XMLHttpRequestのメソッドまたはURLを確定できません。");
            if (xhr.Legacy) Notice($"L{request.Source.Line}: ActiveXObjectを使うHTTP送信は旧IE向けの候補です。");
        }
    }

    private static bool IsXhrConstructor(Expression node, Scope scope, out bool legacy)
    {
        legacy = false;
        if (node is not NewExpression { Callee: Identifier identifier } creation || scope.IsDeclared(identifier.Name)) return false;
        if (identifier.Name == "XMLHttpRequest") return true;
        legacy = identifier.Name == "ActiveXObject" && creation.Arguments.Count > 0
            && StringValue(creation.Arguments[0]) is { } name && name.Contains("XMLHTTP", StringComparison.OrdinalIgnoreCase);
        return legacy;
    }

    private static string? Selector(Expression expression, Scope scope)
    {
        if (expression is Identifier identifier)
            return scope.FindXhr(identifier.Name) is not null ? "XMLHttpRequest: " + identifier.Name : scope.FindSelector(identifier.Name);
        if (expression is CallExpression call)
        {
            if (call.Callee is Identifier { Name: "$" or "jQuery" } jquery && !scope.IsDeclared(jquery.Name) && call.Arguments.Count > 0)
                return StringValue(call.Arguments[0]);
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

    private static bool IsDocument(Expression expression, Scope scope) =>
        expression is Identifier { Name: "document" } && !scope.IsDeclared("document")
        || expression is MemberExpression { Object: Identifier { Name: "window" } } member
            && !scope.IsDeclared("window") && PropertyName(member) == "document";

    private static bool IsJQuery(Expression expression, Scope scope) =>
        expression is CallExpression { Callee: Identifier { Name: "$" or "jQuery" } jquery } && !scope.IsDeclared(jquery.Name)
        || expression is Identifier identifier && scope.FindJQuery(identifier.Name);

    internal int? ResolveCall(CallExpression call, EntryData entry)
    {
        var scope = scopes.TryGetValue(call, out var indexed) ? indexed : entry.Scope;
        if (call.Callee is Identifier id) return scope.Resolve(id.Name);
        if (call.Callee is not MemberExpression { Object: Identifier owner } member || PropertyName(member) is not { } name)
            return null;
        if (scope.FindNamespace(owner.Name) is { } module) return module.GetValueOrDefault(name);
        if (owner.Name != "window" || scope.IsDeclared("window") || scope.Resolve(name) != globals.Resolve(name)
            || globals.Resolve(name) is not { } target) return null;
        return windowFunctions.Contains(target) ? target : null;
    }
    internal RazorJavaScriptRequest? Request(Node node) => requestNodes.GetValueOrDefault(node);
    internal void Notice(string message)
    {
        var notice = "解析は部分的 · " + message;
        if (!notices.Contains(notice)) notices.Add(notice);
    }

    internal SourceRange Range(ScriptUnit unit, Node node)
    {
        if (unit.Approximate) return unit.Source;
        var start = unit.Source.Offset + node.Start;
        var line = unit.Source.Line + unit.Text.AsSpan(0, Math.Clamp(node.Start, 0, unit.Text.Length)).Count('\n');
        return new SourceRange(start, Math.Max(0, MappedEnd(unit, node) - node.Start), line, unit.Source.FilePath ?? pagePath);
    }

    internal static string Slice(ScriptUnit unit, Node node)
    {
        var start = Math.Clamp(node.Start, 0, unit.OriginalText.Length);
        var end = Math.Clamp(MappedEnd(unit, node), start, unit.OriginalText.Length);
        var value = unit.OriginalText[start..end].Trim();
        return value.Length > 140 ? value[..139] + "…" : value;
    }

    // Razor式を同じ長さの「0 + 空白」に射影すると、JS ASTの終端は0の直後になる。
    // 終端トークンが置換された場合だけ、その後の置換空白を元の式の末尾まで戻す。
    // 通常のJSの末尾から、別のRazor制御ブロックへ範囲を伸ばすことはしない。
    private static int MappedEnd(ScriptUnit unit, Node node)
    {
        var end = Math.Clamp(node.End, 0, unit.Text.Length);
        if (!unit.ContainsRazor || unit.Approximate || end == 0) return end;
        var tokenStart = end;
        while (tokenStart > node.Start && (char.IsLetterOrDigit(unit.Text[tokenStart - 1])
            || unit.Text[tokenStart - 1] is '_' or '$')) --tokenStart;
        if (tokenStart == end || unit.Text.AsSpan(tokenStart, end - tokenStart)
            .SequenceEqual(unit.OriginalText.AsSpan(tokenStart, end - tokenStart))) return end;
        var mappedEnd = end;
        for (var cursor = end; cursor < unit.Text.Length && char.IsWhiteSpace(unit.Text[cursor]); ++cursor)
            if (unit.Text[cursor] != unit.OriginalText[cursor]) mappedEnd = cursor + 1;
        return mappedEnd;
    }

    internal static string? PropertyName(MemberExpression member) => member.Computed
        ? StringValue(member.Property) : (member.Property as Identifier)?.Name;
    private static string? StringValue(Expression expression) => (expression as StringLiteral)?.Value;

    internal static IEnumerable<Node> Descendants(Node root)
    {
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            var children = node.ChildNodes.ToArray();
            for (var i = children.Length - 1; i >= 0; --i) pending.Push(children[i]);
        }
    }

    internal sealed record ScriptUnit(string Text, string OriginalText, SourceRange Source, JsProgram Root,
        bool Approximate, bool ContainsRazor, IReadOnlyDictionary<int, SourceRange>? RenderingSlots = null);
    internal sealed class EntryData(MethodEntry entry, Node? body, ScriptUnit unit, Scope scope)
    {
        public MethodEntry Entry { get; set; } = entry;
        public Node? Body { get; } = body;
        public ScriptUnit Unit { get; } = unit;
        public Scope Scope { get; } = scope;
        public string? Failure { get; set; }
    }
    internal sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, List<int?>> names = new(StringComparer.Ordinal);
        public Scope? Parent { get; } = parent;
        public int OwnerEntryId { get; set; }
        public bool Dynamic { get; set; }
        public Dictionary<string, Dictionary<string, int?>> Namespaces { get; } = new(StringComparer.Ordinal);
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
        public Scope? DeclarationScope(string name) => names.ContainsKey(name) ? this : Parent?.DeclarationScope(name);
        public bool IsDeclared(string name) => names.ContainsKey(name) || Parent?.IsDeclared(name) == true;
        public bool FindJQuery(string name) => names.ContainsKey(name) ? JQueryVariables.Contains(name) : Parent?.FindJQuery(name) == true;
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
