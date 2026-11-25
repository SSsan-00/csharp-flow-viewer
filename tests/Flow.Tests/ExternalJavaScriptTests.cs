using Flow.Analysis;
using Flow.Desktop;
using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class ExternalJavaScriptTests
{
    [TestMethod]
    [DataRow("if", "function reload() { return 42; }", true)]
    [DataRow("if-crlf", "function reload() { return 42; }", true)]
    [DataRow("foreach", "function reload() { return 42; }", true)]
    [DataRow("if", "function reload() {} function reload() { return 42; }", false)]
    [DataRow("foreach", "function other() { function reload() { return 42; } }", false)]
    [DataRow("foreach", "function reload() { return 42; } window['reload'] = replacement;", false)]
    [DataRow("if", "function reload() { return 42; } function configure() { eval(code); }", false)]
    [DataRow("if", "const invalid = ; function reload() { return 42; }", false)]
    public async Task GeneratedJavascriptExpressionOffersOnlyAnIntactDefinitionSource(string kind, string definitions, bool links)
    {
        using var tree = new Files();
        var variable = kind.StartsWith("if", StringComparison.Ordinal) ? """
            const label =
                @if (Model.ShortLabel)
                {
                    @:'short'
                }
                else
                {
                    @:'long'
                }
            ;
            """ : """
            const options = [
                @foreach (var option in Model.Options)
                {
                    @: @option,
                }
            ];
            """;
        var text = $$"""
            <select onchange="reload();"></select>
            <script language="javascript">
            {{variable}}
            {{definitions}}
            </script>
            """;
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (kind.EndsWith("crlf", StringComparison.Ordinal))
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        var path = tree.Add("Pages/Generated.cshtml", text);
        using var session = tree.Open(path);
        var page = await ReadPage(session);
        var overview = FindCall(page.Analyze(-1), "onchange → reload()");
        var handler = Assert.ContainsSingle(page.Entries.Where(entry => page.IsInlineEvent(entry.Id)));
        var eventOffset = text.IndexOf("reload();", StringComparison.Ordinal);
        var clicked = SourceNodePicker.Find(page.Analyze(handler.Id).Nodes, path, eventOffset, line: 1);
        Assert.IsNotNull(clicked);
        Assert.AreEqual(NodeKind.Call, clicked.Kind);
        Assert.AreEqual(eventOffset, clicked.Source!.Offset);
        Assert.DoesNotContain(entry => entry.Name == "reload", page.Entries);
        foreach (var node in new[] { overview, clicked })
        {
            Assert.IsNull(node.TargetMethod, "Generated expressions must not certify a runtime call target.");
            Assert.AreEqual(links, node.IsDefinitionCandidate);
            if (!links)
            {
                Assert.IsNull(node.TargetDeclaration);
                continue;
            }
            const string definition = "function reload() { return 42; }";
            var offset = text.IndexOf(definition, StringComparison.Ordinal);
            Assert.AreEqual(path, node.TargetDeclaration?.FilePath);
            Assert.AreEqual(offset, node.TargetDeclaration?.Offset);
            Assert.AreEqual(definition.Length, node.TargetDeclaration?.Length);
            Assert.AreEqual(1 + text.AsSpan(0, offset).Count('\n'), node.TargetDeclaration?.Line);
        }
    }

    [TestMethod]
    [DataRow("", false)]
    [DataRow("defer", false)]
    [DataRow("async", false)]
    [DataRow("type='module'", true)]
    public async Task ReturnSlotCandidateDoesNotHideAnotherVisibleClassicDefinition(string scriptMode, bool opensLocalSource)
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", $$"""
            <select onchange="reload();"></select>
            <script {{scriptMode}} src="~/js/other.js"></script>
            <script language="javascript">
              function compute() {
                  return @if (Model.Enabled)
                  {
                      <text>1;</text>
                  }
                  else
                  {
                      <text>2;</text>
                  }
              }
              function reload() {}
            </script>
            """);
        var script = tree.Add("wwwroot/js/other.js", "function reload() {}");
        using var session = tree.Open(pagePath, script);
        var page = await ReadPage(session);
        var link = FindCall(page.Analyze(-1), "onchange → reload()");
        Assert.IsNull(link.TargetMethod);
        Assert.AreEqual(opensLocalSource, link.IsDefinitionCandidate);
        Assert.AreEqual(opensLocalSource ? pagePath : null, link.TargetDeclaration?.FilePath);
    }

    [TestMethod]
    public async Task InlineChangeFollowsTheOriginalExternalOffsetAfterRecoveringAnUnrelatedAssignment()
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", """
            <script src="~/js/legacy.js"></script>
            <select onchange="reload();"></select>
            """);
        const string source = "const broken=;\r\nfunction reload() { return 7; }";
        var script = tree.Add("wwwroot/js/legacy.js", source);
        using var session = tree.Open(pagePath, script);
        var page = await ReadPage(session);
        var link = FindCall(page.Analyze(-1), "onchange → reload()");
        Assert.AreEqual(script, link.TargetFilePath);
        var file = session.Files.Single(item => item.Path == script);
        foreach (var context in new RazorSourceFile?[] { null, page })
        {
            var opened = await session.ReadJavaScriptAsync(file, CancellationToken.None, context);
            Assert.AreEqual(source, opened.Text);
            var definition = opened.Entries.Single(item => item.Name == "reload");
            Assert.AreEqual(link.TargetMethod, definition.Id);
            Assert.AreEqual(2, definition.Source.Line);
            var body = opened.Analyze(definition.Id);
            Assert.IsTrue(body.IsPartial);
            var result = FindCall(body, "return 7;");
            Assert.AreEqual(source.IndexOf("return 7;", StringComparison.Ordinal), result.Source!.Offset);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EditorClickOnEncodedRazorEventOpensTheExactDefinition(bool external)
    {
        using var tree = new Files();
        var script = tree.Add("wwwroot/js/orders.js", "// external definition\nfunction reload(value) { return value; }");
        var pagePath = tree.Add("Pages/Index.cshtml", """
            <select onchange="return reload(&quot;orders&quot;);"></select>
            """ + "\n" + (external ? """<script src="~/js/orders.js"></script>"""
                                   : "<script>\nfunction reload(value) { return value; }\n</script>"));
        using var session = tree.Open(pagePath, script);
        var page = await ReadPage(session);
        var handler = page.Entries.Single(entry => page.Text.AsSpan(entry.Source.Offset, entry.Source.Length)
            .StartsWith("return reload(&quot;", StringComparison.Ordinal));
        var graph = page.Analyze(handler.Id);
        var calleeOffset = page.Text.IndexOf("reload", StringComparison.Ordinal);
        var selected = SourceNodePicker.Find(graph.Nodes, pagePath, calleeOffset, line: 1);
        Assert.IsNotNull(selected);
        Assert.AreEqual(NodeKind.Call, selected.Kind, selected.Label);
        Assert.AreEqual(calleeOffset, selected.Source!.Offset);
        Assert.AreEqual("reload(&quot;orders&quot;)", page.Text.Substring(selected.Source.Offset, selected.Source.Length));
        var targetFile = Assert.ContainsSingle(session.Files.Where(file => file.Path == selected.TargetFilePath));
        var entries = external
            ? (await session.ReadJavaScriptAsync(targetFile, CancellationToken.None, page)).Entries
            : (await session.ReadRazorAsync(targetFile, CancellationToken.None)).Entries;
        var definition = Assert.ContainsSingle(entries.Where(entry => entry.Id == selected.TargetMethod));
        Assert.AreEqual("reload", definition.Name);
        Assert.AreEqual(external ? 2 : 3, definition.Source.Line);
        var definitionFlow = external
            ? (await session.ReadJavaScriptAsync(targetFile, CancellationToken.None, page)).Analyze(definition.Id)
            : page.Analyze(definition.Id);
        Assert.Contains(node => node.Label == "return value;", definitionFlow.Nodes);
    }

    [TestMethod]
    public void ClickingInlineEventTerminatorChoosesTheAdjacentCallButDoesNotInventAnUnresolvedTarget()
    {
        var page = new RazorSourceFile("""
            <select onchange="reload();missing();"></select>
            <script language="javascript">function reload() { return 0; }</script>
            """, Path.GetFullPath("Index.cshtml"));
        var graph = page.Analyze(page.Entries.Single(entry => entry.Name.StartsWith("change · ", StringComparison.Ordinal)).Id);
        var semicolon = page.Text.IndexOf(';');
        var call = SourceNodePicker.Find(graph.Nodes, page.FilePath, semicolon, line: 1, inlineTerminator: true);
        Assert.IsNotNull(call);
        Assert.AreEqual("reload()", call.Label);
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "reload").Id, call.TargetMethod);
        var missing = SourceNodePicker.Find(graph.Nodes, page.FilePath, page.Text.IndexOf(';', semicolon + 1),
            line: 1, inlineTerminator: true);
        Assert.IsNotNull(missing);
        Assert.AreEqual("missing()", missing.Label);
        Assert.IsNull(missing.TargetMethod);
    }

    [TestMethod]
    public void EncodedNewlineAndAstralCharacterDoNotMoveTheRazorCallOutOfItsSourceLine()
    {
        var page = new RazorSourceFile("""
            <select onchange="noop(&quot;&#x1F680;&quot;);&#10;return reload(@Model.Id);"></select>
            <script language="javascript">function reload(id) { return id; }</script>
            """, Path.GetFullPath("Index.cshtml"));
        var handler = page.Entries.Single(entry => page.IsInlineEvent(entry.Id));
        var graph = page.Analyze(handler.Id);
        var callOffset = page.Text.IndexOf("reload(", StringComparison.Ordinal);
        var selected = SourceNodePicker.Find(graph.Nodes, page.FilePath, callOffset, line: 1);
        Assert.IsNotNull(selected);
        Assert.AreEqual("reload(@Model.Id)", selected.Label);
        Assert.AreEqual(1, selected.Source!.Line);
        Assert.AreEqual(callOffset, selected.Source.Offset);
        Assert.AreEqual(selected.Label, page.Text.Substring(callOffset, selected.Source.Length));
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "reload").Id, selected.TargetMethod);
    }

    [TestMethod]
    public void InlineChangeFindsAStaticWindowAliasButNotCompetingOrDynamicAssignments()
    {
        var page = new RazorSourceFile("""
            <select onchange="reload();"></select>
            <select onblur="ambiguous();"></select>
            <select onfocus="dynamic();"></select>
            <select oninput="conditional();"></select>
            <script language="javascript">
                window.reload = implementation;
                function implementation() { return 1; }
                function fallback() { return 2; }
                window.ambiguous = implementation;
                window.ambiguous = fallback;
                window.dynamic = makeHandler();
                if (flag) window.conditional = implementation;
                function normal() { window.reload(); }
            </script>
            """, Path.GetFullPath("Index.cshtml"));
        var definition = page.Entries.Single(entry => entry.Name == "implementation");
        var overview = page.Analyze(-1);
        Assert.AreEqual(definition.Id, FindCall(overview, "onchange → reload()").TargetMethod);
        var normal = page.Analyze(page.Entries.Single(entry => entry.Name == "normal").Id);
        Assert.AreEqual(definition.Id, FindCall(normal, "window.reload()").TargetMethod);
        foreach (var label in new[] { "onblur → ambiguous()", "onfocus → dynamic()", "oninput → conditional()" })
            Assert.IsNull(FindCall(overview, label).TargetMethod);
    }

    [TestMethod]
    [DataRow("function reload() { return 42; }", true)]
    [DataRow("function reload() { return 42; } var reload;", true)]
    [DataRow("var reload; function reload() { return 42; }", true)]
    [DataRow("window.reload = reload; function reload() { return 42; }", true)]
    [DataRow("function reload() { return 42; } window['reload'] = reload;", true)]
    [DataRow("var reload; window.reload = reload; function reload() { return 42; }", true)]
    [DataRow("function reload() { return 42; } var reload = other;", false)]
    [DataRow("function reload() { return 42; } function reload() { return 0; } var reload;", false)]
    [DataRow("function reload() { return 42; } var reload; if (flag) window.reload = reload;", false)]
    [DataRow("function reload() { return 42; } window.reload = factory(); window.reload = reload;", false)]
    [DataRow("function reload() { return 42; } window.reload = reload; let window;", false)]
    [DataRow("function reload() { return 42; } var reload; function modify() { eval(code); }", false)]
    public async Task InlineChangeNavigatesOnlyToAnUnchangedClassicFunction(string script, bool links)
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", $$"""
            <select onchange='reload();'></select>
            <script language='javascript'>
            {{script}}
            </script>
            """);
        using var session = tree.Open(pagePath);
        var page = await ReadPage(session);
        var overview = FindCall(page.Analyze(-1), "onchange → reload()");
        var handler = Assert.ContainsSingle(page.Entries.Where(entry => entry.Name.StartsWith("change · ", StringComparison.Ordinal)));
        var callOffset = page.Text.IndexOf("reload()", StringComparison.Ordinal);
        var picked = SourceNodePicker.Find(page.Analyze(handler.Id).Nodes, pagePath, callOffset, line: 1);
        Assert.IsNotNull(picked);
        Assert.AreEqual(NodeKind.Call, picked.Kind);
        Assert.AreEqual(callOffset, picked.Source!.Offset);
        if (!links)
        {
            Assert.IsNull(overview.TargetMethod);
            Assert.IsNull(picked.TargetMethod);
            Assert.IsNull(picked.TargetDeclaration);
            return;
        }
        var method = Assert.ContainsSingle(page.Entries.Where(entry => entry.Name == "reload"));
        Assert.AreEqual(pagePath, picked.TargetFilePath);
        Assert.AreEqual(method.Id, overview.TargetMethod);
        Assert.AreEqual(method.Id, picked.TargetMethod);
        var destination = session.Files.Single(file => file.Path == picked.TargetFilePath);
        var reopened = await session.ReadRazorAsync(destination, CancellationToken.None);
        var body = reopened.Analyze(method.Id).Nodes.Single(node => node.Label == "return 42;");
        Assert.AreEqual(page.Text.IndexOf("return 42;", StringComparison.Ordinal), body.Source!.Offset);
    }

    [TestMethod]
    [DataRow("""<select onchange="reload();"></select>""", """<script>function reload() { return 0; }</script>""", true)]
    [DataRow("""<select onchange='reload();'></select>""", """<script>function reload() { return 0; }</script>""", true)]
    [DataRow("""<select onchange=reload();></select>""", """<script>function reload() { return 0; }</script>""", true)]
    [DataRow("""<select onchange="reload();"></select>""", """@section Scripts { <script>function reload() { return 0; }</script> }""", true)]
    [DataRow("""<select onchange="reload();"></select>""", """<script>function reload() { return @Model.Current; }</script>""", true)]
    [DataRow("""<select onchange="reload();"></select>""", """<script type="text/jscript">function reload() { return 0; }</script>""", true)]
    [DataRow("""<select onchange="reload();"></select>""", """<script language="JavaScript1.2">function reload() { return 0; }</script>""", true)]
    [DataRow("""<select onchange="reload();"></select>""", """<script type="" language="VBScript">function reload() { return 0; }</script>""", true)]
    [DataRow("""<select onchange="reload();"></select>""", """<script type="module">function reload() { return 0; }</script>""", false)]
    [DataRow("""<select onchange="reload();"></select>""", """<script type="application/json" language="javascript">function reload() { return 0; }</script>""", false)]
    [DataRow("""<select onchange="reload();"></select>""", """<script type="text/javascript; charset=UTF-8">function reload() { return 0; }</script>""", false)]
    [DataRow("""<select onchange=“reload();”></select>""", """<script>function reload() { return 0; }</script>""", false)]
    public void InlineChangeWithTerminatingSemicolonCanFollowSameFileDefinition(string element, string script, bool available)
    {
        var page = new RazorSourceFile(element + "\n" + script, Path.GetFullPath("Index.cshtml"));
        var overview = page.Analyze(-1);
        var change = Assert.ContainsSingle(overview.Nodes.Where(node => node.Label.StartsWith("onchange → ", StringComparison.Ordinal)));
        if (!available)
        {
            Assert.IsNull(change.TargetMethod);
            return;
        }
        var reload = Assert.ContainsSingle(page.Entries.Where(entry => entry.Name == "reload"), string.Join("\n", page.Notices));
        Assert.AreEqual(reload.Id, change.TargetMethod, change.Label);
        Assert.AreEqual(page.FilePath, change.TargetFilePath);
        Assert.AreEqual(2, reload.Source.Line);
        var inlineEvent = page.Entries.Single(entry => entry.Name.StartsWith("change · ", StringComparison.Ordinal));
        var call = FindCall(page.Analyze(inlineEvent.Id), "reload()");
        Assert.AreEqual(reload.Id, call.TargetMethod);
    }

    [TestMethod]
    [DataRow("text/javascript", true)]
    [DataRow("APPLICATION/X-JAVASCRIPT", true)]
    [DataRow("application/json", false)]
    public async Task InlineChangeWithTerminatingSemicolonFollowsOnlyTheReferencedClassicScript(string type, bool available)
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", $$"""
            @page
            <select onchange="reload();"></select>
            <script type="{{type}}" src="~/js/orders.js"></script>
            <script>function normal() { reload(); }</script>
            """);
        var script = tree.Add("wwwroot/js/orders.js", "// existing page script\nfunction reload() { return 0; }");
        var unrelated = tree.Add("wwwroot/js/unrelated.js", "function reload() {}");
        using var session = tree.Open(pagePath, script, unrelated);
        var page = await ReadPage(session);
        var change = FindCall(page.Analyze(-1), "onchange → reload()");
        var normal = FindCall(page.Analyze(page.Entries.Single(entry => entry.Name == "normal").Id), "reload()");
        var targetFile = session.Files.Single(file => file.Path == script);
        var targetSource = await session.ReadJavaScriptAsync(targetFile, CancellationToken.None, page);
        var definition = targetSource.Entries.Single(entry => entry.Name == "reload");
        Assert.AreEqual(2, definition.Source.Line);
        foreach (var call in new[] { change, normal })
        {
            Assert.AreEqual(available ? script : null, call.TargetFilePath);
            Assert.AreEqual(available ? definition.Id : (int?)null, call.TargetMethod);
        }
        var callers = (await session.FindCallersAsync(targetFile, definition.Id, CancellationToken.None)).Locations;
        if (available) CollectionAssert.AreEqual(new[] { 2, 4 }, callers.Select(caller => caller.Source.Line).ToArray());
        else Assert.IsEmpty(callers);
    }

    [TestMethod]
    public async Task ClassicScriptAndInlineEventJumpToExternalDefinitionWithStableId()
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Orders/Index.cshtml", """
            @page
            <script src="~/js/target.js?v=7"></script>
            <script src="/js/second.js"></script>
            <button type="button" onclick="return confirmOrder(42)">Order</button>
            """);
        var targetPath = tree.Add("wwwroot/js/target.js", "// lead\r\nfunction confirmOrder(value) {\r\n    window.saveOrder(value);\r\n    return false;\r\n}");
        var secondPath = tree.Add("wwwroot/js/second.js", "function saveOrder(value) { persist(value); }");
        using var session = tree.Open(pagePath, targetPath, secondPath);
        CollectionAssert.AreEqual(new[] { pagePath, secondPath, targetPath }, session.Files.Select(file => file.Path).ToArray());
        var page = await ReadPage(session);
        var entry = page.Entries.Single(item => item.Name.StartsWith("click", StringComparison.Ordinal));
        var call = FindCall(page.Analyze(entry.Id), "confirmOrder(42)");
        Assert.AreEqual(targetPath, call.TargetFilePath);
        Assert.AreEqual(pagePath, call.Source?.FilePath);

        var targetFile = session.Files.Single(file => file.Path == targetPath);
        var linked = await session.ReadJavaScriptAsync(targetFile, CancellationToken.None, page);
        var standalone = await session.ReadJavaScriptAsync(targetFile, CancellationToken.None);
        var definition = linked.Entries.Single(item => item.Name == "confirmOrder");
        Assert.AreEqual(2, definition.Source.Line);
        Assert.AreEqual(targetPath, definition.Source.FilePath);
        Assert.AreEqual(definition.Id, call.TargetMethod);
        Assert.AreEqual(definition.Id, standalone.Entries.Single(item => item.Name == "confirmOrder").Id);
        var scriptLink = page.Analyze(-1).Nodes.Single(item => item.Label == "target.js");
        Assert.AreEqual(linked.Entries[0].Id, scriptLink.TargetMethod);
        Assert.AreEqual(0, linked.Entries[0].Id);

        var body = linked.Analyze(definition.Id);
        Assert.Contains(item => item.Label == "return false;", body.Nodes);
        var downstream = FindCall(body, "window.saveOrder(value)");
        Assert.AreEqual(secondPath, downstream.TargetFilePath);
        var second = await session.ReadJavaScriptAsync(session.Files.Single(file => file.Path == secondPath),
            CancellationToken.None, jsContext: linked);
        Assert.AreEqual(second.Entries.Single(item => item.Name == "saveOrder").Id, downstream.TargetMethod);
        Assert.IsNull(FindCall(standalone.Analyze(definition.Id), "window.saveOrder(value)").TargetMethod);
    }

    [TestMethod]
    public async Task StaticModuleImportsResolveLocalNamedDefaultAndNamespaceFunctions()
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", """
            @page
            <script type="module">
              import useDefault, { run as callRun } from "/js/app.js";
              import * as api from "/js/app.js";
              import { never } from "some-package";
              function start() { callRun(); useDefault(); api.run(); never(); }
            </script>
            """);
        var module = tree.Add("wwwroot/js/app.js", """
            import { execute } from "./worker.js";
            export function run() { execute(); }
            export default function () { }
            """);
        var worker = tree.Add("wwwroot/js/worker.js", "export const execute = () => log();");
        using var session = tree.Open(pagePath, module, worker);
        var page = await ReadPage(session);
        var graph = page.Analyze(page.Entries.Single(item => item.Name == "start").Id);
        var direct = FindCall(graph, "callRun()");
        Assert.AreEqual(module, direct.TargetFilePath);
        Assert.AreEqual(module, FindCall(graph, "useDefault()").TargetFilePath);
        Assert.AreEqual(direct.TargetMethod, FindCall(graph, "api.run()").TargetMethod);
        Assert.IsNull(FindCall(graph, "never()").TargetMethod);
        Assert.Contains(notice => notice.Contains("some-package", StringComparison.Ordinal), page.Notices);

        var moduleFile = session.Files.Single(file => file.Path == module);
        foreach (var context in new RazorSourceFile?[] { null, page })
        {
            var opened = await session.ReadJavaScriptAsync(moduleFile, CancellationToken.None, context);
            var definition = opened.Entries.Single(item => item.Name == "run");
            Assert.AreEqual(direct.TargetMethod, definition.Id);
            Assert.AreEqual(worker, FindCall(opened.Analyze(definition.Id), "execute()").TargetFilePath);
        }
    }

    [TestMethod]
    public async Task OutsideUnlistedAndUncertainScriptGlobalsDoNotBecomeTargets()
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", """
            @page
            <script src="https://example.invalid/external.js"></script>
            <script src="//example.invalid/external.js"></script>
            <script src="/../outside.js"></script>
            <script src="/js/excluded.js"></script>
            <script src="/js/late.js" defer></script>
            @if (Model.Active)
            {
                <script src="/js/optional.js"></script>
            }
            <script src="/js/one.js"></script>
            <script src="/js/two.js"></script>
            <script>
              function start() { unavailable(); late(); optional(); duplicate(); }
            </script>
            """);
        var outside = tree.Add("outside.js", "function unavailable() {}");
        tree.Add("wwwroot/js/excluded.js", "function unavailable() {}");
        var deferred = tree.Add("wwwroot/js/late.js", "function late() {}");
        var optional = tree.Add("wwwroot/js/optional.js", "function optional() {}");
        var one = tree.Add("wwwroot/js/one.js", "function duplicate() {}");
        var two = tree.Add("wwwroot/js/two.js", "function duplicate() {}");
        using var session = tree.Open(pagePath, outside, deferred, optional, one, two);
        var page = await ReadPage(session);
        var graph = page.Analyze(page.Entries.Single(item => item.Name == "start").Id);
        foreach (var label in new[] { "unavailable()", "late()", "optional()", "duplicate()" })
            Assert.IsNull(FindCall(graph, label).TargetMethod, label);
        var overview = page.Analyze(-1);
        Assert.Contains(node => node.TargetFilePath == deferred, overview.Nodes);
        Assert.Contains(node => node.TargetFilePath == optional, overview.Nodes);
        Assert.DoesNotContain(node => node.TargetFilePath == outside, overview.Nodes);
        Assert.AreEqual(4, page.Notices.Count(notice => notice.Contains("外部スクリプトを読み込めません", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task IndependentClassicJavaScriptRetainsLegacySyntax()
    {
        using var tree = new Files();
        var script = tree.Add("wwwroot/js/legacy.js", "function legacy(x) { with (x) { log(); } return false; }");
        using var session = tree.Open(null, script);
        var source = await session.ReadJavaScriptAsync(Assert.ContainsSingle(session.Files), CancellationToken.None);
        var legacy = source.Entries.Single(entry => entry.Name == "legacy");
        Assert.Contains(node => node.Label == "return false;", source.Analyze(legacy.Id).Nodes);
    }

    [TestMethod]
    public async Task OverviewEventsLinkToKnownFunctionsAndCallerSearchUsesOnlyReferencesInScope()
    {
        using var tree = new Files();
        var first = tree.Add("Pages/First.cshtml", """
            @page
            <script src="/js/application.js"></script>
            <button onclick="foo()">Run</button>
            <select onchange="if (this.value) foo(); bar(); missing(); setTimeout(() => nested());"></select>
            <div oninput="unresolved()"></div>
            <script>
                var xhr = new XMLHttpRequest();
                xhr.open("GET", "/First");
                xhr.addEventListener("error", bar);
                xhr.send();
                document.addEventListener("DOMContentLoaded", bar);
            </script>
            """);
        var second = tree.Add("Pages/Second.cshtml", """
            <script src="/js/application.js"></script>
            <button onclick="foo()">Run</button>
            """);
        var application = tree.Add("wwwroot/js/application.js", "function foo() {} function bar() {} function nested() {}");
        var other = tree.Add("wwwroot/js/other.js", "function foo() {} function unrelated() { foo(); }");
        using var session = tree.OpenPages([first, second], application, other);
        var pageFile = session.Files.Single(file => file.Path == first);
        var targetFile = session.Files.Single(file => file.Path == application);
        var page = await session.ReadRazorAsync(pageFile, CancellationToken.None);
        var standalone = await session.ReadJavaScriptAsync(targetFile, CancellationToken.None);
        var target = standalone.Entries.Single(entry => entry.Name == "foo");
        var overview = page.Analyze(-1);
        var click = Assert.ContainsSingle(overview.Nodes.Where(node => node.Label == "onclick → foo()"));
        Assert.AreEqual(target.Id, click.TargetMethod);
        Assert.AreEqual(application, click.TargetFilePath);
        Assert.AreEqual("onclick=\"foo()\"", page.Text.Substring(click.Source!.Offset, click.Source.Length));
        var change = Assert.ContainsSingle(overview.Nodes.Where(node => node.Label.StartsWith("onchange → foo() / bar() / missing()", StringComparison.Ordinal)));
        Assert.IsNotNull(change.CallTargets);
        CollectionAssert.AreEqual(new[] { "foo", "bar" }, change.CallTargets.Select(item => item.Label).ToArray());
        Assert.DoesNotContain(node => node.Label.StartsWith("error", StringComparison.Ordinal)
            || node.Label.StartsWith("DOMContentLoaded", StringComparison.Ordinal), overview.Nodes);
        Assert.Contains(node => node.Kind == NodeKind.Transfer && node.Label.Contains("GET /First", StringComparison.Ordinal), overview.Nodes);
        Assert.IsNull(Assert.ContainsSingle(overview.Nodes.Where(node => node.Label == "oninput → unresolved()")).TargetMethod);
        var callers = await session.FindCallersAsync(targetFile, target.Id, CancellationToken.None);
        Assert.IsFalse(callers.IsPartial);
        CollectionAssert.AreEqual(new[] { first, first, second }, callers.Locations.Select(caller => caller.File.Path).ToArray());
        CollectionAssert.AreEqual(new[] { 3, 4, 2 }, callers.Locations.Select(caller => caller.Source.Line).ToArray());
        Assert.IsTrue(callers.Locations.All(caller => caller.EntryId == -1));
    }

    [TestMethod]
    public async Task CallerSearchInCSharpUsesBoundOverloadInsteadOfTextualMatches()
    {
        using var tree = new Files();
        using var session = tree.OpenCSharp(
            ("Service.cs", "class Service { public void Run(int x) {} public void Run(string x) {} }"),
            ("Caller.cs", """
                class Caller {
                    void Go(Service svc) {
                        svc.Run(1);
                        svc.Run("wrong overload");
                        System.Action<int> referenceOnly = svc.Run;
                        var stringOnly = "svc.Run(2)";
                    }
                }
                """));
        var serviceFile = session.Files.Single(file => file.Label == "Service.cs");
        var service = await session.ReadAsync(serviceFile, CancellationToken.None);
        var target = Assert.ContainsSingle(service.Methods.Where(method => method.Signature.Contains("int x", StringComparison.Ordinal)));
        var callers = await session.FindCallersAsync(serviceFile, target.Id, CancellationToken.None);
        var actual = Assert.ContainsSingle(callers.Locations);
        Assert.AreEqual("Caller.cs", actual.File.Label);
        Assert.AreEqual(3, actual.Source.Line);
        Assert.AreEqual("svc.Run(1)", File.ReadAllText(actual.File.Path).Substring(actual.Source.Offset, actual.Source.Length));
        Assert.IsNotNull(actual.EntryId);
    }

    [TestMethod]
    [DataRow("~/js/orders.js?v=@Model.Version", true)]
    [DataRow("/js/orders.js#@Model.Version", true)]
    [DataRow("~/js/@Model.Script?v=fixed", false)]
    [DataRow("@Model.ScriptUrl?file=/js/orders.js", false)]
    public async Task RazorEventUsesOnlyTheLiteralScriptPathBeforeTheDynamicSuffix(string url, bool expectJump)
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", $$"""
            @page
            <script defer src="{{url}}"></script>
            <select onchange="reload()"></select>
            """);
        var script = tree.Add("wwwroot/js/orders.js", "function reload() { return 42; }");
        var decoy = tree.Add("wwwroot/js/decoy.js", "function reload() { return -1; }");
        using var session = tree.Open(pagePath, script, decoy);
        var page = await ReadPage(session);
        var node = Assert.ContainsSingle(page.Analyze(-1).Nodes.Where(node => node.Source?.Line == 3 && node.Kind == NodeKind.Call));
        if (!expectJump)
        {
            Assert.IsNull(node.TargetMethod);
            Assert.IsNull(node.TargetFilePath);
            return;
        }

        Assert.AreEqual(script, node.TargetFilePath);
        var file = session.Files.Single(file => file.Path == script);
        var definitionSource = await session.ReadJavaScriptAsync(file, CancellationToken.None, page);
        var definition = Assert.ContainsSingle(definitionSource.Entries.Where(entry => entry.Name == "reload"));
        Assert.AreEqual(definition.Id, node.TargetMethod);
        Assert.AreEqual(1, definition.Source.Line);
        Assert.AreEqual(node.Source, Assert.ContainsSingle((await session.FindCallersAsync(file, definition.Id, CancellationToken.None)).Locations).Source);
    }

    [TestMethod]
    public void EventRegistrationsAndOrdinaryCallsFollowTheSameExplicitWindowHandler()
    {
        var page = new RazorSourceFile("""
            <script>
              window.save = function () { return true; };
              document.getElementById("submit").onclick = window.save;
              document.getElementById("choice").addEventListener("change", window["save"]);
              $("#edit").on("focus", window.save);
              document.getElementById("dynamic").oninput = window[handlerName];
              function normal() { window.save(); }
              function shadowed(window) {
                  document.getElementById("shadowed").onblur = window.save;
              }
              document.getElementById("result").onmouseup = window.save();
            </script>
            <button onclick="window.save()"></button>
            """, Path.GetFullPath("Index.cshtml"));

        var save = page.Entries.Single(entry => entry.Name == "save");
        var normal = page.Analyze(page.Entries.Single(entry => entry.Name == "normal").Id);
        Assert.AreEqual(save.Id, FindCall(normal, "window.save()").TargetMethod);
        var overview = page.Analyze(-1);
        Assert.AreEqual(save.Id, FindCall(overview, "onclick → window.save()").TargetMethod);
        foreach (var selector in new[] { "#submit", "#choice", "#edit" })
        {
            var registration = Assert.ContainsSingle(overview.Nodes.Where(node => node.Label.EndsWith($" · {selector}")));
            Assert.AreEqual(save.Id, registration.TargetMethod, selector);
        }
        foreach (var label in new[] { "input · #dynamic", "blur · #shadowed", "mouseup · #result" })
            Assert.IsNull(FindCall(overview, label).TargetMethod, label);
    }

    [TestMethod]
    public void InlineEventCanCallTheWindowPropertyOfAClassicVarButNotALexicalBinding()
    {
        var page = new RazorSourceFile("""
            <script>
              var save = function () { return true; };
              const preview = () => true;
              function normal() { save(); window.save(); }
            </script>
            <button onclick="return window.save()"></button>
            <select onchange="window['save']()"></select>
            <button onblur="window.preview()"></button>
            <button onfocus="preview()"></button>
            """, Path.GetFullPath("Index.cshtml"));
        var save = page.Entries.Single(entry => entry.Name == "save");
        var graph = page.Analyze(-1);
        foreach (var label in new[] { "onclick → window.save()", "onchange → window.save()" })
            Assert.AreEqual(save.Id, FindCall(graph, label).TargetMethod, label);
        var normal = page.Analyze(page.Entries.Single(entry => entry.Name == "normal").Id);
        Assert.AreEqual(save.Id, FindCall(normal, "save()").TargetMethod);
        Assert.AreEqual(save.Id, FindCall(normal, "window.save()").TargetMethod);
        Assert.IsNull(FindCall(graph, "onblur → window.preview()").TargetMethod);
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "preview").Id,
            FindCall(graph, "onfocus → preview()").TargetMethod);
    }

    [TestMethod]
    [DataRow("@Url.Content(\"~/js/orders.js\")", true)]
    [DataRow("@Url.Content(Model.Script)", false)]
    [DataRow("@Url.Content(\"~/js/\" + Model.Script)", false)]
    [DataRow("@Url.Content(\"~/js/orders.js\")//not-the-same-path()", false)]
    public async Task InlineAndScriptCallsShareOnlyTheLiteralUrlContentTarget(string url, bool available)
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", $$"""
            @page
            <script src="{{url}}"></script>
            <script>function normal() { save(); }</script>
            <button onclick="return save()"></button>
            <select onchange="save()"></select>
            """);
        var script = tree.Add("wwwroot/js/orders.js", "// header\nfunction save() {}");
        using var session = tree.Open(pagePath, script);
        var page = await ReadPage(session);
        var opened = await session.ReadJavaScriptAsync(session.Files.Single(file => file.Path == script), CancellationToken.None);
        var definition = opened.Entries.Single(entry => entry.Name == "save");
        var overview = page.Analyze(-1);
        foreach (var call in new[] { FindCall(overview, "onclick → save()"), FindCall(overview, "onchange → save()"),
            FindCall(page.Analyze(page.Entries.Single(entry => entry.Name == "normal").Id), "save()") })
        {
            Assert.AreEqual(available ? script : null, call.TargetFilePath);
            Assert.AreEqual(available ? definition.Id : (int?)null, call.TargetMethod);
        }
    }

    [TestMethod]
    public async Task InlineChangeFollowsDeferredClassicScriptWithoutTreatingItAsAvailableDuringParsing()
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", """
            @page
            <script defer src="/js/orders.js"></script>
            <script>function parserCall() { reload(); } </script>
            <select onchange="reload()"></select>
            """);
        var script = tree.Add("wwwroot/js/orders.js", "function reload() { return 1; }");
        using var session = tree.Open(pagePath, script);
        var page = await ReadPage(session);
        var node = Assert.ContainsSingle(page.Analyze(-1).Nodes.Where(node => node.Label == "onchange → reload()"));
        Assert.AreEqual(script, node.TargetFilePath);
        Assert.AreEqual(4, node.Source!.Line);
        var early = page.Entries.Single(entry => entry.Name == "parserCall");
        Assert.IsNull(FindCall(page.Analyze(early.Id), "reload()").TargetMethod);
        var targetFile = session.Files.Single(file => file.Path == script);
        var js = await session.ReadJavaScriptAsync(targetFile, CancellationToken.None, page);
        var definition = js.Entries.Single(entry => entry.Name == "reload");
        Assert.AreEqual(definition.Id, node.TargetMethod);
        var callers = await session.FindCallersAsync(targetFile, definition.Id, CancellationToken.None);
        Assert.AreEqual(node.Source, Assert.ContainsSingle(callers.Locations).Source);
    }

    [TestMethod]
    public void InlineChangeFollowsUnambiguousExplicitWindowFunction()
    {
        var page = new RazorSourceFile("""
            @page
            <script>
              window.reload = () => { return 1; };
              window.changed = function () { return 2; };
              window.changed = other;
              window.onBranch = () => 1;
              if (flag) window.onBranch = () => 2;
            </script>
            <select onchange="reload()"></select>
            <select oninput="window.reload()"></select>
            <button onclick="changed()"></button>
            <input onblur="onBranch()">
            """, Path.GetFullPath("Index.cshtml"));
        var graph = page.Analyze(-1);
        var reload = page.Entries.Single(entry => entry.Name == "reload");
        Assert.AreEqual(reload.Id, FindCall(graph, "onchange → reload()").TargetMethod);
        Assert.AreEqual(reload.Id, FindCall(graph, "oninput → window.reload()").TargetMethod);
        Assert.IsNull(FindCall(graph, "onclick → changed()").TargetMethod);
        Assert.IsNull(FindCall(graph, "onblur → onBranch()").TargetMethod);
    }

    [TestMethod]
    public async Task InlineChangeDoesNotLinkAmbiguousOrOptionalDeferredDefinitions()
    {
        using var tree = new Files();
        var pagePath = tree.Add("Pages/Index.cshtml", """
            @page
            <script defer src="/js/one.js"></script>
            <script defer src="/js/two.js"></script>
            <script defer async src="/js/optional.js"></script>
            @if (true) { <script defer src="/js/guarded.js"></script> }
            <select onchange="reload()"></select>
            <button onclick="optional()"></button>
            <input oninput="guarded()" />
            """);
        var one = tree.Add("wwwroot/js/one.js", "function reload() {}");
        var two = tree.Add("wwwroot/js/two.js", "function reload() {}");
        var optional = tree.Add("wwwroot/js/optional.js", "function optional() {}");
        var guarded = tree.Add("wwwroot/js/guarded.js", "function guarded() {}");
        using var session = tree.Open(pagePath, one, two, optional, guarded);
        var page = await ReadPage(session);
        var graph = page.Analyze(-1);
        foreach (var label in new[] { "onchange → reload()", "onclick → optional()", "oninput → guarded()" })
            Assert.IsNull(FindCall(graph, label).TargetMethod);
    }

    [TestMethod]
    public async Task CancelledCallerSearchCanRetryAndWarmSearchPreservesPerTargetSourceLines()
    {
        using var tree = new Files();
        var page = tree.Add("Pages/Index.cshtml", """
            <script src="/js/app.js"></script>
            <a href="/" onclick="first()">First</a>
            <input onchange="second()">
            """);
        var script = tree.Add("wwwroot/js/app.js", "function first() {} function second() {}");
        using var session = tree.Open(page, script);
        var file = session.Files.Single(item => item.Path == script);
        var source = await session.ReadJavaScriptAsync(file, CancellationToken.None);
        var first = source.Entries.Single(item => item.Name == "first");
        var second = source.Entries.Single(item => item.Name == "second");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await session.FindCallersAsync(file, first.Id, cancelled.Token));
        var firstResult = await session.FindCallersAsync(file, first.Id, CancellationToken.None);
        var warmedOtherTarget = await session.FindCallersAsync(file, second.Id, CancellationToken.None);
        Assert.AreEqual(2, Assert.ContainsSingle(firstResult.Locations).Source.Line);
        Assert.AreEqual(3, Assert.ContainsSingle(warmedOtherTarget.Locations).Source.Line);
        Assert.AreSame(firstResult, await session.FindCallersAsync(file, first.Id, CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await session.FindCallersAsync(file, first.Id, cancelled.Token));
    }

    private static FlowNode FindCall(FlowDiagram graph, string label) => graph.Nodes.Single(item => item.Label == label);

    private static Task<RazorSourceFile> ReadPage(ProjectSession session) =>
        session.ReadRazorAsync(session.Files.Single(file => file.IsRazor), CancellationToken.None);

    private sealed class Files : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("cfv-script-test-").FullName;
        private readonly AdhocWorkspace workspace = new();

        public string Add(string path, string text)
        {
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, text);
            return fullPath;
        }

        public ProjectSession Open(string? page, params string[] scripts) => OpenPages(page is null ? [] : [page], scripts);

        public ProjectSession OpenPages(string[] pages, params string[] scripts) =>
            new(MSBuildWorkspace.Create(), CreateProject(), [], null, new ProjectOptions("Debug", "net9.0"), pages, scripts);

        public ProjectSession OpenCSharp(params (string Name, string Code)[] files)
        {
            var project = CreateProject();
            foreach (var (name, code) in files)
                project = project.AddDocument(name, code, filePath: Add(name, code)).Project;
            return new ProjectSession(MSBuildWorkspace.Create(), project, [], null, new ProjectOptions("Debug", "net9.0"));
        }

        private Project CreateProject() => workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
            "Application", "Application", LanguageNames.CSharp, filePath: Path.Combine(root, "Application.csproj"),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

        public void Dispose()
        {
            workspace.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
