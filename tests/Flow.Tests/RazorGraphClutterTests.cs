using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorGraphClutterTests
{
    [TestMethod]
    public void InlineRazorScriptsOmitBasicAssignmentsAndLookupButKeepSideEffects()
    {
        var page = Page("""
            <select onchange="reload()"></select>
            <script>
              function value() { return 'OK'; }
              function elementId() { return 'order'; }
              function reload() {
                let result = 0;
                let element = document.getElementById('order');
                result = value();
                result ||= value();
                document.querySelector(elementId()).textContent = value();
                window.document['querySelectorAll']('.orders');
                element.disabled = true;
                element.style.display = 'none';
                element.classList.add('hidden');
                element.setAttribute('aria-label', value());
                element.onchange = reload;
                element.addEventListener('change', reload);
                service.querySelector('custom');
                service.setAttribute('custom', 'value');
              }
            </script>
            """);
        var overview = page.Analyze(-1);
        var handler = ByLabel(overview, "onchange → reload()");
        var graph = page.Analyze(handler.TargetMethod!.Value);
        Assert.DoesNotContain(node => node.Label is "result = 0" or "element = document.getElementById('order')"
            or "result = value()" or "result ||= value()", graph.Nodes);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Call && node.Label.StartsWith("document.", StringComparison.Ordinal), graph.Nodes);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Call && node.Label.StartsWith("window.document", StringComparison.Ordinal), graph.Nodes);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Call && (node.Label.StartsWith("element.classList", StringComparison.Ordinal)
            || node.Label.StartsWith("element.setAttribute", StringComparison.Ordinal)), graph.Nodes);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Transfer && (node.Label.Contains(".textContent =", StringComparison.Ordinal)
            || node.Label.StartsWith("element.disabled =", StringComparison.Ordinal) || node.Label.StartsWith("element.style.display =", StringComparison.Ordinal)), graph.Nodes);
        var directValue = Next(graph, graph.Nodes.Single(node => node.Kind == NodeKind.Start));
        Assert.AreEqual("value()", directValue.Label);
        var definition = page.Entries.Single(entry => entry.Name == "value");
        Assert.AreEqual(definition.Id, directValue.TargetMethod);
        Assert.AreEqual(page.FilePath, directValue.TargetFilePath);
        var guard = Next(graph, directValue);
        Assert.AreEqual((NodeKind.Condition, "result"), (guard.Kind, guard.Label));
        var rhs = Next(graph, guard, "False");
        Assert.AreEqual(definition.Id, rhs.TargetMethod);
        var argument = ByLabel(graph, "elementId()");
        Assert.AreEqual(argument.Id, Next(graph, guard, "True").Id);
        Assert.AreEqual(argument.Id, Next(graph, rhs).Id);
        var modifier = Next(graph, argument);
        Assert.AreEqual(definition.Id, modifier.TargetMethod);
        var attributeValue = Next(graph, modifier);
        Assert.AreEqual(definition.Id, attributeValue.TargetMethod);
        var handlerWrite = Next(graph, attributeValue);
        Assert.AreEqual("element.onchange = reload", handlerWrite.Label);
        Assert.AreEqual("element.addEventListener('change', reload)", Next(graph, handlerWrite).Label);
        Assert.AreEqual(NodeKind.Call, ByLabel(graph, "service.querySelector('custom')").Kind);
        Assert.AreEqual(NodeKind.Call, ByLabel(graph, "service.setAttribute('custom', 'value')").Kind);
    }

    [TestMethod]
    public void GeneratedRazorBranchKeepsTestsAndFunctionJumpsWithoutLookupOrAssignmentNodes()
    {
        var page = Page("""
            <select onchange="reload()"></select>
            <script>
              function render() { return 'READY'; }
              function reload() {
                @if (Model.Ready) {
                  <text>
                    let message = '';
                    if (document.getElementById('target')) { message = render(); }
                    window.document.querySelector('#target').textContent = message;
                    document.querySelector('#target').classList.toggle('ready');
                  </text>
                }
              }
            </script>
            """);
        var handler = ByLabel(page.Analyze(-1), "onchange → reload()");
        var graph = page.Analyze(handler.TargetMethod!.Value);
        var server = ByLabel(graph, "描画条件: Model.Ready");
        var renderBranch = Next(graph, server, "True");
        var client = Next(graph, renderBranch, "生成後、このJSが実行される場合");
        Assert.AreEqual((NodeKind.Condition, "document.getElementById('target')"), (client.Kind, client.Label));
        var call = Next(graph, client, "True");
        Assert.AreEqual("render()", call.Label);
        Assert.IsNotNull(call.TargetMethod);
        Assert.AreEqual(3, page.Entries.Single(entry => entry.Id == call.TargetMethod).NavigationSource.Line);
        Assert.AreEqual(page.FilePath, call.TargetFilePath);
        Assert.AreEqual(8, call.Source!.Line);
        var done = Next(graph, client, "False");
        Assert.AreEqual("JS断片の終了", done.Label);
        Assert.AreEqual(done, Next(graph, call));
        Assert.HasCount(1, graph.Nodes.Where(node => node.Id.Contains("_candidate_", StringComparison.Ordinal) && node.Kind == NodeKind.Call));
    }

    [TestMethod]
    public void ShadowedDocumentNamesAreNotTreatedAsBrowserLookups()
    {
        var page = Page("""
            <script>
              function first(document) { let result = document.querySelector('own'); }
              function second(window) {
                @if (Model.Ready) {
                  @:if (enabled) window.document.querySelector('own');
                }
              }
            </script>
            """);
        Assert.AreEqual(NodeKind.Call, ByLabel(Analyze(page, "first"), "document.querySelector('own')").Kind);
        Assert.AreEqual(NodeKind.Call, ByLabel(Analyze(page, "second"), "window.document.querySelector('own')").Kind);
    }

    [TestMethod]
    public void TheRazorOverviewKeepsTheEventAndConditionButNotRoutineOperations()
    {
        var page = Page("""
            @{ var value = 1; value = 2; }
            @if (value == 2) {
              @{ value = 3; }
              <text><button onclick="document.getElementById('mode').disabled = true; reload();">Load</button></text>
            }
            <script>function reload() { return 42; }</script>
            """);
        var graph = page.Analyze(-1);
        Assert.AreEqual(NodeKind.Condition, ByLabel(graph, "描画条件: value == 2").Kind);
        Assert.DoesNotContain(node => node.Label is "var value = 1" or "value = 2" or "value = 3", graph.Nodes);
        var reload = page.Entries.Single(entry => entry.Name == "reload");
        var handler = ByLabel(graph, "onclick → reload()");
        Assert.AreEqual(reload.Id, handler.TargetMethod);
        var attribute = page.Entries.Single(entry => page.IsInlineEvent(entry.Id));
        var inline = page.Analyze(attribute.Id);
        Assert.AreEqual(reload.Id, Assert.ContainsSingle(inline.Nodes.Where(node => node.Kind == NodeKind.Call)).TargetMethod);
    }

    [TestMethod]
    public void ComputedWritesAndDestructuringKeepTheRealEvaluationWithoutBookkeepingNodes()
    {
        var page = Page("""
            <button onclick="run()"></button>
            <script>
            function key() { return 'value'; }
            function load() { return {}; }
            function fallback() { return 2; }
            function tail() {}
            function run() {
                rows[0]++;
                ++rows[key()];
                rows[key()] += load();
                const {[key()]: first = fallback()} = load();
                [rows[key()]] = load();
                for (let i = 0; ready(); i++, --rows[0]) {
                    if (skip()) continue;
                    tail();
                }
            }
            </script>
            """);
        var root = ByLabel(page.Analyze(-1), "onclick → run()");
        var graph = page.Analyze(root.TargetMethod!.Value);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Transfer && node.Label != "continue", graph.Nodes);
        var start = graph.Nodes.Single(node => node.Kind == NodeKind.Start);
        var computeUpdate = Next(graph, start);
        var computeCompound = Next(graph, computeUpdate);
        var compoundValue = Next(graph, computeCompound);
        var loadObject = Next(graph, compoundValue);
        var objectKey = Next(graph, loadObject);
        Assert.AreEqual(("key()", "key()", "load()", "load()", "key()"),
            (computeUpdate.Label, computeCompound.Label, compoundValue.Label, loadObject.Label, objectKey.Label));
        var defaultCheck = Next(graph, objectKey);
        Assert.AreEqual(NodeKind.Condition, defaultCheck.Kind);
        var fallback = Next(graph, defaultCheck, "True");
        Assert.AreEqual("fallback()", fallback.Label);
        Assert.AreEqual(Next(graph, defaultCheck, "False"), Next(graph, fallback));
        foreach (var call in new[] { computeUpdate, computeCompound, compoundValue, loadObject, objectKey, fallback })
            Assert.AreEqual(page.Entries.Single(entry => entry.Name + "()" == call.Label).Id, call.TargetMethod);
        var loadArray = Next(graph, fallback);
        Assert.AreEqual("load()", loadArray.Label);
        var index = Next(graph, loadArray);
        Assert.AreEqual("key()", index.Label);
        Assert.AreEqual("for 判定へ", Next(graph, index).Label);
        Assert.AreEqual(Next(graph, index), Next(graph, ByLabel(graph, "continue")));
        Assert.AreEqual(Next(graph, index), Next(graph, ByLabel(graph, "tail()")));
    }

    [TestMethod]
    public void PreserveRazorDecisionWithoutPreviewingBookkeepingOnlyGeneratedText()
    {
        var page = Page("""
            <script>
            function run() {
                @if (Model.Ready) {
                    <text>++i; list[0]--; ({n} = row);</text>
                } else {
                    @:i++; const item = load();
                }
            }
            </script>
            """);
        var graph = Analyze(page, "run");
        var guard = ByLabel(graph, "描画条件: Model.Ready");
        var housekeeping = Next(graph, guard, "True");
        Assert.AreEqual("条件成立時に描画 · 生成JS", housekeeping.Label);
        StringAssert.Contains(page.Text.Substring(housekeeping.Source!.Offset, housekeeping.Source.Length), "++i;");
        Assert.AreEqual("条件不成立時に描画 · 生成JS: load()", Next(graph, guard, "False").Label);
        Assert.AreEqual("Razor側の生成終了", Next(graph, housekeeping).Label);
    }

    [TestMethod]
    public void RazorDomFocusAndSubmissionAreHiddenButNotUnrelatedOrOverriddenMethods()
    {
        var page = Page("""
            <script>
            function settings() { return {}; }
            function run(other) {
                var obj = document.getElementById('entry');
                obj.focus(settings());
                document.forms[0].requestSubmit();
                document.querySelector('form').submit();
                obj.blur();
                other.focus();
                other.submit();
            }
            function custom() {
                const input = document.querySelector('input');
                input.focus = settings;
                input.focus();
            }
            function generated() {
                @if (Model.Ready) {
                    <text>
                    const field = document.querySelector('input');
                    if (enabled) { field.focus(settings()); }
                    document.forms[0].submit();
                    </text>
                }
            }
            </script>
            """);
        var normal = Analyze(page, "run");
        CollectionAssert.AreEquivalent(new[] { "settings()", "other.focus()", "other.submit()" }, normal.Nodes
            .Where(node => node.Kind == NodeKind.Call).Select(node => node.Label).ToArray());
        Assert.AreEqual(NodeKind.Call, ByLabel(Analyze(page, "custom"), "input.focus()").Kind);
        var generated = Analyze(page, "generated");
        var candidate = ByLabel(generated, "enabled");
        var settings = Next(generated, candidate, "True");
        Assert.AreEqual("settings()", settings.Label);
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "settings").Id, settings.TargetMethod);
        Assert.AreEqual("JS断片の終了", Next(generated, settings).Label);
        Assert.AreEqual(Next(generated, settings), Next(generated, candidate, "False"));
    }

    [TestMethod]
    public void UnresolvedObjFocusStatementsAreHiddenButTheirArgumentsAndResultUsesRemain()
    {
        var page = Page("""
            <button onclick="obj.focus(); run(event.target)"></button>
            <script>
            function prepare() { return {}; }
            function run(obj) {
                obj.focus(prepare());
                if (obj.focus()) obj.submit();
                use(obj.focus());
                finish();
            }
            </script>
            """);
        var overview = page.Analyze(-1);
        var eventNode = ByLabel(overview, "onclick → run()");
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "run").Id, eventNode.TargetMethod);
        var inline = page.Analyze(page.Entries.Single(entry => page.IsInlineEvent(entry.Id)).Id);
        Assert.AreEqual("run(event.target)", Assert.ContainsSingle(inline.Nodes.Where(n => n.Kind == NodeKind.Call)).Label);

        var graph = page.Analyze(eventNode.TargetMethod!.Value);
        var setup = Next(graph, graph.Nodes.Single(n => n.Kind == NodeKind.Start));
        Assert.AreEqual("prepare()", setup.Label);
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "prepare").Id, setup.TargetMethod);
        Assert.DoesNotContain(n => n.Label == "obj.focus(prepare())", graph.Nodes);
        var test = Next(graph, Next(graph, setup));
        Assert.AreEqual((NodeKind.Condition, "obj.focus()"), (test.Kind, test.Label));
        Assert.AreEqual("obj.submit()", Next(graph, test, "True").Label);
        Assert.AreEqual("use(obj.focus())", Next(graph, Next(graph, test, "False")).Label);
        Assert.AreEqual("finish()", Next(graph, ByLabel(graph, "use(obj.focus())")).Label);
    }

    [TestMethod]
    public void GeneratedObjFocusStatementsAreHiddenButExplicitCustomReceiversRemain()
    {
        var page = Page("""
            <script>
            function prepare() { return {}; }
            function run(obj) {
                @if (Model.Ready) {
                    <text>obj.focus(prepare()); finish();</text>
                }
            }
            function own() {
                const widget = { focus() { return 1; } };
                const obj = widget;
                obj.focus();
                @if (Model.Ready) {
                    <text>obj.focus(); finish();</text>
                }
            }
            function generatedOwn() {
                @if (Model.Ready) {
                    <text>const obj = { focus: prepare }; obj.focus(); finish();</text>
                }
            }
            function assigned(obj) {
                obj.focus = prepare;
                obj.focus();
            }
            </script>
            """);

        var graph = Analyze(page, "run");
        var output = Next(graph, ByLabel(graph, "描画条件: Model.Ready"), "True");
        Assert.AreEqual("条件成立時に描画 · 生成JS: prepare() · finish()", output.Label);
        var preparation = Next(graph, output, "生成後、このJSが実行される場合");
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "prepare").Id, preparation.TargetMethod);
        Assert.AreEqual("finish()", Next(graph, preparation).Label);
        Assert.HasCount(2, Analyze(page, "own").Nodes.Where(n => n.Kind == NodeKind.Call && n.Label == "obj.focus()"));
        Assert.AreEqual(NodeKind.Call, ByLabel(Analyze(page, "generatedOwn"), "obj.focus()").Kind);
        Assert.AreEqual(NodeKind.Call, ByLabel(Analyze(page, "assigned"), "obj.focus()").Kind);
    }

    [TestMethod]
    public void RazorJQueryCleanupAndAttributeCallsKeepArgumentsBranchesAndEventNavigation()
    {
        var page = Page("""
            <script>
            function oldEvent() { return 'click.legacy'; }
            function attributeName() { return 'href'; }
            function replacement() { return '/new'; }
            function clicked() { return true; }
            function run(custom) {
                $('a').off(oldEvent()).removeAttr(attributeName()).on('click', clicked);
                let links = jQuery('a');
                links.attr('href', replacement());
                if (links.attr(attributeName())) clicked();
                custom.attr('href');
                custom.off();
                custom.removeAttr('href');
            }
            function shadowed($, jQuery) {
                $('a').off(); jQuery('a').attr('href');
            }
            </script>
            """);
        var graph = Analyze(page, "run");
        var start = graph.Nodes.Single(node => node.Kind == NodeKind.Start);
        var oldEvent = Next(graph, start);
        Assert.AreEqual("oldEvent()", oldEvent.Label);
        Assert.IsNotNull(oldEvent.TargetMethod);
        var name = Next(graph, oldEvent);
        Assert.AreEqual("attributeName()", name.Label);
        Assert.IsNotNull(name.TargetMethod);
        var registration = Next(graph, name);
        Assert.AreEqual(".on('click', clicked)", registration.Label);
        Assert.AreEqual("replacement()", Next(graph, registration).Label);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Call && (node.Label.StartsWith("$('a')", StringComparison.Ordinal)
            || node.Label.StartsWith("links.attr", StringComparison.Ordinal)), graph.Nodes);
        var predicate = ByLabel(graph, "links.attr(attributeName())");
        Assert.AreEqual(NodeKind.Condition, predicate.Kind);
        var clicked = page.Entries.Single(entry => entry.Name == "clicked");
        Assert.AreEqual(clicked.Id, Next(graph, predicate, "True").TargetMethod);
        Assert.AreEqual(clicked.Id, ByLabel(page.Analyze(-1), "click → clicked() · a").TargetMethod);
        foreach (var custom in new[] { "custom.attr('href')", "custom.off()", "custom.removeAttr('href')" })
            Assert.AreEqual(NodeKind.Call, ByLabel(graph, custom).Kind);
        var shadowed = Analyze(page, "shadowed");
        Assert.AreEqual(NodeKind.Call, ByLabel(shadowed, "$('a').off()").Kind);
        Assert.AreEqual(NodeKind.Call, ByLabel(shadowed, "jQuery('a').attr('href')").Kind);
    }

    [TestMethod]
    public void StandaloneJavaScriptOmitsAssignmentsButKeepsTheExistingDomCalls()
    {
        const string text = "const element = document.getElementById('target');";
        var path = Path.GetFullPath("Sample.js");
        var javascript = new RazorJavaScript(text, path, [new(text, new SourceRange(0, text.Length, 1, path))]);
        var graph = javascript.Analyze(javascript.Entries.Single().Id);
        Assert.AreEqual(NodeKind.Call, ByLabel(graph, "document.getElementById('target')").Kind);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Transfer, graph.Nodes);
    }

    private static RazorSourceFile Page(string text) => new(text, Path.GetFullPath("GraphClutter.cshtml"));
    private static FlowDiagram Analyze(RazorSourceFile page, string name) => page.Analyze(page.Entries.Single(entry => entry.Name == name).Id);
    private static FlowNode ByLabel(FlowDiagram graph, string label) => graph.Nodes.Single(node => node.Label == label);
    private static FlowNode Next(FlowDiagram graph, FlowNode node, string? label = null) =>
        graph.Nodes.Single(target => target.Id == graph.Edges.Single(edge => edge.From == node.Id && edge.Label == label).To);
}
