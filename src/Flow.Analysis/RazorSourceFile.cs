using System.Net;
using System.Text;
using Flow.Model;

namespace Flow.Analysis;

/// <summary>A verified PageModel declaration supplied by the project semantic model.</summary>
public sealed record RazorHandler(string MethodName, string HttpMethod, string? HandlerName,
    string FilePath, int MethodId, SourceRange Source);

/// <summary>
/// The analysis entry point for a .cshtml file. Razor determines markup boundaries,
/// JavaScript supplies client flows, and the project binds server handler symbols.
/// </summary>
public sealed class RazorSourceFile
{
    private readonly string filePath;
    private readonly RazorMarkupDocument document;
    private readonly RazorMarkupReader markup;
    private readonly RazorJavaScript javaScript;
    private readonly bool hasBaseHref;
    private readonly List<string> scriptNotices = [];
    private readonly List<(SourceRange Source, string Path)> scriptLinks = [];
    private readonly Dictionary<int, RazorElement> requests = [];
    private readonly Dictionary<int, string> inlineLabels = [];
    private readonly List<string> bindingNotices = [];
    private IReadOnlyList<RazorHandler> handlers = [];

    public RazorSourceFile(string text, string filePath, IReadOnlyList<RazorImport>? imports = null,
        CancellationToken token = default) : this(text, filePath, imports, token, null) { }

    internal RazorSourceFile(string text, string filePath, IReadOnlyList<RazorImport>? imports,
        CancellationToken token, LocalJavaScriptFiles? local)
    {
        Text = text;
        this.filePath = filePath;
        document = new RazorMarkupDocument(text, filePath, imports, token);
        markup = new RazorMarkupReader(document, text, token);
        hasBaseHref = markup.Elements.Any(element => element.TagName == "base" && element.Attribute("href") is not null);
        var scripts = markup.Scripts.Select(script => (script.Source.Offset, script)).ToList();
        foreach (var reference in markup.References)
        {
            token.ThrowIfCancellationRequested();
            var fragment = !reference.IsDynamic
                && local?.ResolveTag(reference.Url, filePath, RouteTemplate, hasBaseHref) is { } path
                    ? local.Read(path, reference.IsModule, token) : null;
            if (fragment is null)
            {
                scriptNotices.Add($"解析は部分的 · L{reference.Source.Line}: 外部スクリプトを読み込めません: {reference.Url}");
                continue;
            }
            // Show the file, but do not infer an unconditional page global for an optional script.
            var isolated = reference.HasDeferredExecution || document.GuardsAt(reference.Source.Offset).Count > 0;
            scripts.Add((reference.Source.Offset, fragment with { IsolatedClassic = isolated }));
            scriptLinks.Add((reference.Source, fragment.Source.FilePath!));
        }
        javaScript = new RazorJavaScript(text, filePath,
            scripts.OrderBy(item => item.Offset).Select(item => item.script),
            (specifier, origin) => local?.ResolveImport(specifier, origin, filePath, RouteTemplate, hasBaseHref) is { } path
                ? local.Read(path, module: true, token) : null, token);
        var entries = new List<MethodEntry>
        {
            new(-1, "ページ全体", "ページ全体", document.Range(0, text.Length))
        };
        foreach (var element in markup.Elements)
        {
            token.ThrowIfCancellationRequested();
            var label = ElementLabel(element);
            foreach (var attribute in element.Attributes.Values)
            {
                if (attribute.Name.Length <= 2 || !attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) continue;
                var eventLabel = $"{attribute.Name[2..]} · {label}";
                var code = attribute.IsDynamic ? document.ProjectJavaScript(attribute.ValueStart, attribute.ValueLength) : attribute.RawValue;
                var id = javaScript.AddInlineHandler(eventLabel, code,
                    document.Range(attribute.ValueStart, attribute.ValueLength), attribute.IsDynamic);
                inlineLabels[id] = eventLabel;
            }
            if (element.TagName == "a" && element.Attribute("href") is { } href
                && href.Value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                var colon = href.RawValue.IndexOf(':');
                if (colon >= 0)
                {
                    var code = href.IsDynamic
                        ? document.ProjectJavaScript(href.ValueStart + colon + 1, href.ValueLength - colon - 1)
                        : href.RawValue[(colon + 1)..];
                    var id = javaScript.AddInlineHandler($"click · {label}", code,
                        document.Range(href.ValueStart + colon + 1, href.ValueLength - colon - 1), href.IsDynamic);
                    inlineLabels[id] = $"click · {label}（javascript:）";
                }
                continue;
            }
            if (element.TagName == "form" || IsSubmitControl(element)
                || element.TagName == "a" && (element.Attribute("href") is not null || element.Attributes.Keys.Any(name =>
                    name is "asp-page" or "asp-controller" or "asp-action" or "asp-route")))
            {
                var eventName = element.TagName == "form" ? "submit" : "click";
                entries.Add(new MethodEntry(element.Start, eventName, $"{eventName} · {label}",
                    document.Range(element.Start, Math.Max(1, element.OpenEnd - element.Start))));
                requests.Add(element.Start, element);
            }
        }
        entries.AddRange(javaScript.Entries.Where(entry => LocalJavaScriptFiles.PathComparer.Equals(entry.Source.FilePath, filePath)));
        Entries = entries;
    }

    public string Text { get; }
    public string? ModelName => document.ModelName;
    public bool IsPage => document.IsPage;
    public string? RouteTemplate => document.RouteTemplate;
    public string? NamespaceName => document.NamespaceName;
    public IReadOnlyList<string> Usings => document.Usings;
    public IReadOnlyList<MethodEntry> Entries { get; }
    public IReadOnlyList<string> Notices => document.Notices.Concat(markup.Notices).Concat(javaScript.Notices)
        .Concat(bindingNotices).Concat(scriptNotices).Concat(hasBaseHref
            ? ["base hrefがあるため、相対URLのJavaScript送信・リンクを現在のPageModelへ自動接続しません。"] : [])
        .Distinct(StringComparer.Ordinal).ToArray();

    internal JavaScriptSourceFile? OpenScript(string path) => javaScript.OpenScript(path,
        (id, token) => ConnectJavaScriptRequests(id, javaScript.Analyze(id, token)));

    /// <summary>Bind only declarations belonging to the page's resolved @model type.</summary>
    public void BindHandlers(IReadOnlyList<RazorHandler> pageHandlers, IReadOnlyList<string>? notices = null)
    {
        handlers = pageHandlers;
        bindingNotices.Clear();
        if (notices is not null) bindingNotices.AddRange(notices);
    }

    /// <summary>Build an overview, a form/navigation flow, or a JavaScript function flow.</summary>
    public FlowDiagram Analyze(int entryId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (entryId == -1) return BuildOverview(token);
        if (requests.TryGetValue(entryId, out var element)) return BuildRequestFlow(element, token);
        if (!Entries.Any(entry => entry.Id == entryId)) throw new ArgumentOutOfRangeException(nameof(entryId));
        var flow = ConnectJavaScriptRequests(entryId, javaScript.Analyze(entryId, token));
        flow = IncludeScriptRendering(entryId, flow, token);
        return flow with { Notices = flow.Notices.Concat(Notices).Distinct(StringComparer.Ordinal).ToArray() };
    }

    private FlowDiagram IncludeScriptRendering(int entryId, FlowDiagram flow, CancellationToken token)
    {
        if (javaScript.ScriptRenderingScope(entryId) is not { } scope) return flow;
        var controls = document.ControlsWithin(scope.Range);
        if (controls.Count == 0) return flow;
        var nodes = flow.Nodes.ToList();
        var edges = flow.Edges.ToList();
        var rendering = new RazorRenderProjector(document, nodes, edges, token, scope.NestedFunctions);
        if (rendering.Project(scope.Range, controls) is null) return flow;
        // The server emits source before a browser can run it; there is no browser edge into Razor.
        var notices = flow.Notices.Append("Razorの生成フローとブラウザー側の実行候補は別々に表示します。").ToList();
        if (rendering.IsTruncated)
            notices.Add("サーバー側の描画フローは最大100件の制御構文を表示しています。");
        return flow with { Nodes = nodes, Edges = edges, Notices = notices };
    }

    /// <summary>
    /// Attach project-resolved server targets to actual JS request nodes. Response
    /// handlers are separate event candidates, never the next synchronous statement.
    /// </summary>
    private FlowDiagram ConnectJavaScriptRequests(int entryId, FlowDiagram flow)
    {
        var nodes = flow.Nodes.ToList();
        var edges = flow.Edges.ToList();
        var noticeList = flow.Notices.ToList();
        var callbackIndex = 0;
        var entryRequests = javaScript.Requests.Where(request => request.OwnerEntryId == entryId).ToArray();
        foreach (var request in entryRequests)
        {
            // Entity-decoded inline attributes may map several expressions to the
            // same broad source range. Such a range is insufficient to choose one.
            if (entryRequests.Count(candidate => candidate.Source == request.Source) != 1) continue;
            var nodeIndex = nodes.FindIndex(node => node.Kind == NodeKind.Transfer && node.Source == request.Source);
            if (nodeIndex < 0) continue;
            var requestNode = nodes[nodeIndex];
            var handler = ResolveJavaScriptHandler(request);
            if (handler is not null)
                nodes[nodeIndex] = requestNode with { TargetMethod = handler.MethodId, TargetFilePath = handler.FilePath };
            else if (request.IsResolved && IsCurrentUrl(request.Url))
                noticeList.Add($"L{request.Source.Line}: HTTP送信先のPageModelハンドラーを一意に解決できません。");

            foreach (var callbackId in request.CallbackEntryIds.Distinct())
            {
                var callback = javaScript.Entries.FirstOrDefault(entry => entry.Id == callbackId);
                if (callback is null) continue;
                var id = $"responseCandidate{callbackIndex++}";
                var target = javaScript.ResolveNavigationTarget(callback.Id);
                nodes.Add(new FlowNode(id, NodeKind.Call, $"応答イベント候補: {callback.Signature}",
                    callback.Source, target.Method, TargetFilePath: target.Path));
                edges.Add(new FlowEdge(requestNode.Id, id, request.IsAsync ? "非同期の応答イベント候補" : "応答イベント候補"));
            }
        }
        return flow with { Nodes = nodes, Edges = edges, Notices = noticeList.Distinct(StringComparer.Ordinal).ToArray() };
    }

    private RazorHandler? ResolveJavaScriptHandler(RazorJavaScriptRequest request)
    {
        if (!request.IsResolved || hasBaseHref || !IsCurrentUrl(request.Url)) return null;
        return ResolveHandler(new RazorRequest(request.HttpMethod, QueryHandler(request.Url), true, false,
            request.HttpMethod + " " + request.Url));
    }

    private FlowDiagram BuildOverview(CancellationToken token)
    {
        var pageSource = document.Range(0, Math.Min(Text.Length, Math.Max(1, Text.IndexOf('\n') + 1)));
        var nodes = new List<FlowNode>
        {
            new("page", NodeKind.Start, Path.GetFileName(filePath) + (IsPage ? "\nRazor Page" : "\nRazor View / Partial"), pageSource)
        };
        var edges = new List<FlowEdge>();
        var notices = Notices.ToList();
        notices.Add("イベントとHTTP送信は静的な候補です。描画時の条件、JavaScriptによる取消・変更、外部スクリプトによって実際の動作は変わります。");
        var rendering = new RazorRenderProjector(document, nodes, edges, token);
        if (rendering.Project(pageSource) is { } renderStart)
            edges.Add(new FlowEdge("page", renderStart, "サーバー側の描画フロー"));
        if (rendering.IsTruncated)
            notices.Add("サーバー側の描画フローは最大100件の制御構文を表示しています。");
        for (var index = 0; index < scriptLinks.Count; index++)
        {
            var (source, path) = scriptLinks[index];
            var scriptId = $"script{index}";
            nodes.Add(new FlowNode(scriptId, NodeKind.Call, "外部 JavaScript: " + Path.GetFileName(path),
                source, TargetMethod: 0, TargetFilePath: path));
            var scope = rendering.ScopeAt(source.Offset);
            edges.Add(new FlowEdge(scope ?? "page", scriptId, scope is null ? "外部スクリプト" : "この描画経路の外部スクリプト"));
        }
        var eventCount = 0;
        foreach (var entry in Entries.Where(entry => requests.ContainsKey(entry.Id) || inlineLabels.ContainsKey(entry.Id)).Take(200))
        {
            token.ThrowIfCancellationRequested();
            AddEvent($"event{eventCount++}", entry.Signature, entry.Source, entry.Id);
        }
        foreach (var binding in javaScript.Bindings.Take(Math.Max(0, 200 - eventCount)))
        {
            token.ThrowIfCancellationRequested();
            var qualifier = binding.IsLegacy ? " · 旧API" : "";
            if (binding.IsConditional) qualifier += " · 条件付き";
            AddEvent($"event{eventCount++}", $"{binding.EventName} · {binding.Selector}{qualifier}",
                binding.Source, binding.HandlerEntryId);
        }
        var requestIndex = 0;
        foreach (var request in javaScript.Requests.Take(50))
        {
            var requestId = "http" + requestIndex++;
            var handler = ResolveJavaScriptHandler(request);
            nodes.Add(new FlowNode(requestId, NodeKind.Transfer, $"{request.Client} · {request.HttpMethod} {request.Url}",
                request.Source, handler?.MethodId, TargetFilePath: handler?.FilePath));
            var scope = IsPageSource(request.Source) ? rendering.ScopeAt(request.Source.Offset) : null;
            edges.Add(new FlowEdge(scope ?? "page", requestId, "JavaScript 内のHTTP候補"));
        }
        if (eventCount == 0)
        {
            nodes.Add(new FlowNode("empty", NodeKind.End, "静的に識別できる画面イベントはありません", pageSource));
            edges.Add(new FlowEdge("page", "empty"));
        }
        if (requests.Count + inlineLabels.Count + javaScript.Bindings.Count > 200)
            notices.Add("ページ全体では先頭200件のイベントを表示しています。各処理は一覧から選択できます。");
        if (javaScript.Requests.Count > 50) notices.Add("ページ全体では先頭50件のJavaScript HTTP送信を表示しています。各処理は一覧から選択できます。");
        return new FlowDiagram(nodes, edges, notices, true);

        void AddEvent(string id, string label, SourceRange source, int? target)
        {
            var onPage = IsPageSource(source);
            var scope = onPage ? rendering.ScopeAt(source.Offset) : null;
            var navigation = target is < 0 ? javaScript.ResolveNavigationTarget(target) : (target, null);
            nodes.Add(new FlowNode(id, NodeKind.Call, label, source, navigation.Item1, TargetFilePath: navigation.Item2));
            var qualifier = scope is not null ? "この描画経路のイベント候補"
                : onPage && document.GuardsAt(source.Offset).Count > 0 ? "条件付きイベント候補" : "イベント候補";
            edges.Add(new FlowEdge(scope ?? "page", id, qualifier));
            if (target is { } targetId && requests.TryGetValue(targetId, out var element))
            {
                var request = DescribeRequest(element);
                var requestId = id + "_request";
                nodes.Add(new FlowNode(requestId, NodeKind.Transfer, request.Label, source));
                edges.Add(new FlowEdge(id, requestId, "既定動作の候補"));
                var handler = ResolveHandler(request);
                if (handler is not null)
                {
                    var handlerId = id + "_handler";
                    nodes.Add(new FlowNode(handlerId, NodeKind.Call, handler.MethodName, source,
                        handler.MethodId, TargetFilePath: handler.FilePath));
                    edges.Add(new FlowEdge(requestId, handlerId, "Razor Pages 規約"));
                }
            }
        }
    }

    private bool IsPageSource(SourceRange source) => source.FilePath is null ||
        LocalJavaScriptFiles.PathComparer.Equals(source.FilePath, filePath);

    private FlowDiagram BuildRequestFlow(RazorElement element, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = document.Range(element.Start, Math.Max(1, element.OpenEnd - element.Start));
        var request = DescribeRequest(element);
        var nodes = new List<FlowNode>();
        var edges = new List<FlowEdge>();
        var notices = Notices.ToList();
        nodes.Add(new FlowNode("start", NodeKind.Start, ElementLabel(element), source));
        var previous = "start";
        var index = 0;
        foreach (var guard in document.GuardsAt(element.Start))
        {
            var id = "guard" + index++;
            nodes.Add(new FlowNode(id, NodeKind.Condition, $"描画条件: {guard.Label}", guard.Source));
            edges.Add(new FlowEdge(previous, id));
            previous = id;
        }
        nodes.Add(new FlowNode("request", NodeKind.Transfer, request.Label, source));
        edges.Add(new FlowEdge(previous, "request", "既定動作の候補"));
        var handler = ResolveHandler(request);
        if (handler is not null)
        {
            nodes.Add(new FlowNode("handler", NodeKind.Call, handler.MethodName, source,
                handler.MethodId, TargetFilePath: handler.FilePath));
            edges.Add(new FlowEdge("request", "handler", "Razor Pages 規約"));
            previous = "handler";
        }
        else
        {
            previous = "request";
            notices.Add(request.IsCurrentPage && IsPage
                ? "送信先ハンドラーを一意に解決できません。@model、HTTPメソッド、handler名、重複宣言を確認してください。"
                : "現在のPageModelへ送ると確定できないため、サーバーメソッドとの接続は表示しません。");
        }
        nodes.Add(new FlowNode("end", NodeKind.End, "送信・遷移の候補", source));
        edges.Add(new FlowEdge(previous, "end"));
        notices.Add("JavaScriptの取消・送信先変更、ブラウザーの入力検証、フォーム外の状態はこの既定動作の図に含みません。対応するイベントの処理も確認してください。");
        if (element.Attribute("disabled") is not null) notices.Add("この要素にはdisabled属性があります。静的な候補として表示しています。");
        return new FlowDiagram(nodes, edges, notices, true);
    }

    private RazorHandler? ResolveHandler(RazorRequest request)
    {
        if (!IsPage || !request.IsCurrentPage || request.IsDynamic) return null;
        var methodHandlers = handlers.Where(handler => handler.HttpMethod.Equals(request.HttpMethod, StringComparison.OrdinalIgnoreCase)).ToArray();
        var matches = methodHandlers.Where(handler => string.Equals(handler.HandlerName ?? "", request.HandlerName ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
        // The framework falls back to an unnamed handler if no named handler matches.
        if (matches.Length == 0 && !string.IsNullOrEmpty(request.HandlerName))
            matches = methodHandlers.Where(handler => string.IsNullOrEmpty(handler.HandlerName)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private RazorRequest DescribeRequest(RazorElement element)
    {
        if (element.TagName == "a")
        {
            var page = TagHelperAttribute(element, "asp-page");
            var href = element.Attribute("href");
            var handler = TagHelperAttribute(element, "asp-page-handler");
            if (href is { IsDynamic: false } && href.Value.TrimStart().StartsWith('#'))
                return new RazorRequest("", null, false, false, "ページ内移動 · " + href.Value);
            var dynamicLink = new[] { page, href, handler }.Any(attribute => attribute?.IsDynamic == true);
            var current = page is not null ? IsCurrentPageName(page.Value) : href is not null && IsCurrentUrl(href.Value);
            if (hasBaseHref && page is null) current = false;
            var otherLinkRouting = UsesNonPageRouting(element);
            if (otherLinkRouting || !string.IsNullOrEmpty(TagHelperAttribute(element, "asp-area")?.Value)) current = false;
            return new RazorRequest("GET", handler?.Value ?? QueryHandler(href?.Value), current, dynamicLink,
                $"GET · {(otherLinkRouting ? "MVC / 名前付きルート（未解決）" : page?.Value ?? href?.Value ?? "送信先未解決")}" + (dynamicLink ? "（動的）" : ""));
        }
        var form = FindForm(element);
        if (form is null) return new RazorRequest("", null, false, true, "フォームを解決できません");
        var method = (element.TagName != "form" ? element.Attribute("formmethod") : null) ?? form.Attribute("method");
        var pageTarget = TagHelperAttribute(element, "asp-page") ?? TagHelperAttribute(form, "asp-page");
        var action = (element.TagName != "form" ? element.Attribute("formaction") : null) ?? form.Attribute("action");
        var namedHandler = TagHelperAttribute(element, "asp-page-handler") ?? TagHelperAttribute(form, "asp-page-handler");
        var area = TagHelperAttribute(element, "asp-area") ?? TagHelperAttribute(form, "asp-area");
        var dynamic = new[] { method, pageTarget, action, namedHandler, area }.Any(attribute => attribute?.IsDynamic == true);
        // A native form defaults to GET. The MVC FormTagHelper instead asks its
        // generator for POST when it creates an action URL and method was omitted.
        var defaultMethod = UsesGeneratedFormAction(form) ? "POST" : "GET";
        var httpMethod = string.IsNullOrWhiteSpace(method?.Value) ? defaultMethod : method.Value.ToUpperInvariant();
        var handlerName = namedHandler?.Value ?? QueryHandler(action?.Value);
        var isCurrent = pageTarget is not null ? IsCurrentPageName(pageTarget.Value) : IsCurrentUrl(action?.Value);
        if (hasBaseHref && pageTarget is null && !string.IsNullOrWhiteSpace(action?.Value)) isCurrent = false;
        if (!string.IsNullOrEmpty(area?.Value)) isCurrent = false;
        var otherRouting = UsesNonPageRouting(element) || UsesNonPageRouting(form);
        if (otherRouting) isCurrent = false;
        var destination = dynamic ? "動的な送信先" : otherRouting ? "MVC / 名前付きルート（未解決）"
            : isCurrent ? "現在のページ" : pageTarget?.Value ?? action?.Value ?? "未解決";
        var label = $"{httpMethod} · {destination}";
        if (!string.IsNullOrEmpty(handlerName)) label += $"\nhandler={handlerName}";
        return new RazorRequest(httpMethod, handlerName, isCurrent, dynamic, label);
    }

    private RazorElement? FindForm(RazorElement element)
    {
        if (element.TagName == "form") return element;
        if (element.Attribute("form") is { } formId)
        {
            if (formId.IsDynamic) return null;
            var matching = markup.Elements.Where(candidate => candidate.TagName == "form"
                && candidate.Attribute("id") is { IsDynamic: false } id && id.Value == formId.Value).ToArray();
            return matching.Length == 1 ? matching[0] : null;
        }
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
            if (parent.TagName == "form") return parent;
        return null;
    }

    private bool IsCurrentPageName(string value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        var name = Path.GetFileNameWithoutExtension(filePath);
        return value == name || value == "./" + name;
    }

    private static bool IsCurrentUrl(string? value) => string.IsNullOrWhiteSpace(value)
        || value.TrimStart().StartsWith('?') || value.TrimStart().StartsWith('#');

    private static string? QueryHandler(string? value)
    {
        if (value is null) return null;
        var queryStart = value.IndexOf('?');
        if (queryStart < 0) return null;
        foreach (var component in value[(queryStart + 1)..].Split('#')[0].Split('&'))
        {
            var pair = component.Split('=', 2);
            if (WebUtility.UrlDecode(pair[0]).Equals("handler", StringComparison.OrdinalIgnoreCase))
                return pair.Length == 2 ? WebUtility.UrlDecode(pair[1]) : "";
        }
        return null;
    }

    private RazorAttribute? TagHelperAttribute(RazorElement element, string name)
    {
        var helper = element.TagName switch
        {
            "form" => "FormTagHelper",
            "button" or "input" => "FormActionTagHelper",
            "a" => "AnchorTagHelper",
            _ => null
        };
        return helper is not null && document.HasTagHelper(helper) ? element.Attribute(name) : null;
    }

    private bool UsesGeneratedFormAction(RazorElement form) => document.HasTagHelper("FormTagHelper")
        && form.Attributes.Keys.Any(name => name is "asp-page" or "asp-page-handler" or "asp-action"
            or "asp-controller" or "asp-area" or "asp-route" || name.StartsWith("asp-route-", StringComparison.Ordinal));

    private bool UsesNonPageRouting(RazorElement element) => TagHelperAttribute(element, "asp-controller") is not null
        || TagHelperAttribute(element, "asp-action") is not null || TagHelperAttribute(element, "asp-route") is not null;

    private static bool IsSubmitControl(RazorElement element)
    {
        var type = element.Attribute("type");
        if (type?.IsDynamic == true) return false;
        return element.TagName == "button" && (type is null || type.Value.Equals("submit", StringComparison.OrdinalIgnoreCase))
            || element.TagName == "input" && (type?.Value.Equals("submit", StringComparison.OrdinalIgnoreCase) == true
                || type?.Value.Equals("image", StringComparison.OrdinalIgnoreCase) == true);
    }

    private string ElementLabel(RazorElement element)
    {
        var identifier = element.Attribute("id") ?? element.Attribute("name");
        var name = element.TagName + (identifier is null ? "" : "#" + Compact(identifier.Value, 40));
        var label = element.Attribute("value")?.Value;
        if (element.TagName is "button" or "a" && element.ContentEnd > element.OpenEnd)
        {
            var builder = new StringBuilder();
            var insideTag = false;
            foreach (var character in document.Html.AsSpan(element.OpenEnd, element.ContentEnd - element.OpenEnd))
            {
                if (character == '<') insideTag = true;
                else if (character == '>') insideTag = false;
                else if (!insideTag) builder.Append(character);
            }
            label = WebUtility.HtmlDecode(builder.ToString());
        }
        return string.IsNullOrWhiteSpace(label) ? name : name + " 「" + Compact(label, 40) + "」";
    }

    private static string Compact(string value, int maximum)
    {
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var compact = string.Join(' ', words);
        return compact.Length <= maximum ? compact : compact[..maximum] + "…";
    }

    private sealed record RazorRequest(string HttpMethod, string? HandlerName, bool IsCurrentPage, bool IsDynamic, string Label);
}
