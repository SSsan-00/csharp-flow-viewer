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
    public void TheStandaloneJavaScriptGraphIsUnchanged()
    {
        const string text = "const element = document.getElementById('target');";
        var path = Path.GetFullPath("Sample.js");
        var javascript = new RazorJavaScript(text, path, [new(text, new SourceRange(0, text.Length, 1, path))]);
        var graph = javascript.Analyze(javascript.Entries.Single().Id);
        Assert.AreEqual(NodeKind.Call, ByLabel(graph, "document.getElementById('target')").Kind);
        Assert.AreEqual(NodeKind.Transfer, ByLabel(graph, "element = document.getElementById('target')").Kind);
    }

    private static RazorSourceFile Page(string text) => new(text, Path.GetFullPath("GraphClutter.cshtml"));
    private static FlowDiagram Analyze(RazorSourceFile page, string name) => page.Analyze(page.Entries.Single(entry => entry.Name == name).Id);
    private static FlowNode ByLabel(FlowDiagram graph, string label) => graph.Nodes.Single(node => node.Label == label);
    private static FlowNode Next(FlowDiagram graph, FlowNode node, string? label = null) =>
        graph.Nodes.Single(target => target.Id == graph.Edges.Single(edge => edge.From == node.Id && edge.Label == label).To);
}
