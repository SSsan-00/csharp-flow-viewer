using Flow.Analysis;
using Flow.Mermaid;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorScriptRenderingTests
{
    [TestMethod]
    [DataRow("<text>result = load();</text>")]
    [DataRow("@:result = load();")]
    public void CompleteGeneratedCallsHaveNodesAndDefinitionJumpsWithoutAClientCondition(string emitted)
    {
        var source = $$"""
            <button onclick="reload()"></button>
            <script>
              function load() { return 42; }
              function reload() {
                @if (Model.Ready) {
                    {{emitted}}
                }
                after();
              }
            </script>
            """;
        var page = new RazorSourceFile(source, Path.GetFullPath("GeneratedCall.cshtml"));
        var handler = page.Analyze(-1).Nodes.Single(node => node.Label == "onclick → reload()");
        var graph = page.Analyze(handler.TargetMethod!.Value);
        var server = Next(graph, Condition(graph, "Model.Ready"), "True");
        var call = Next(graph, server, "生成後、このJSが実行される場合");
        Assert.AreEqual(NodeKind.Call, call.Kind);
        Assert.AreEqual("load()", call.Label);
        var target = Assert.ContainsSingle(page.Entries.Where(entry => entry.Id == call.TargetMethod));
        Assert.AreEqual("load", target.Name);
        Assert.AreEqual(3, target.NavigationSource.Line);
        Assert.AreEqual(page.FilePath, call.TargetFilePath);
        Assert.AreEqual(source.IndexOf("load();", StringComparison.Ordinal), call.Source?.Offset);
        Assert.AreEqual("JS断片の終了", Next(graph, call).Label);
        Assert.AreEqual("Razor側の生成終了", Next(graph, server).Label);
        Assert.DoesNotContain(edge => edge.From == call.Id && !IsRender(edge.To), graph.Edges);
    }

    [TestMethod]
    [DataRow("<script>function load() { return 42; }\n@{ <text>load();</text> }</script>")]
    [DataRow("@{ <text><script>function load() { return 42; }\nload();</script></text> }")]
    public void UnconditionalRazorTextAndADirectlyRenderedScriptAreVisibleInScriptInitialization(string source)
    {
        var page = new RazorSourceFile(source, Path.GetFullPath("TextScript.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        var call = Assert.ContainsSingle(graph.Nodes.Where(node => node.Kind == NodeKind.Call));
        Assert.AreEqual("load()", call.Label);
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "load").Id, call.TargetMethod);
        Assert.AreEqual("load()", Slice(source, call.Source!));
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void ATextWrappedNestedRazorIfKeepsBothGeneratedCallsAndJumps(string newline)
    {
        var source = """
            <button onclick="run(event.target)"></button>
            <script>
            function success() {}
            function failure() {}
            function run(obj) {
                @if (Model.Outer) {
                    <text>
                        @if (Model.Inner) { <text>obj.focus(); success();</text> }
                        else { <text>failure();</text> }
                    </text>
                }
                done();
            }
            </script>
            """.ReplaceLineEndings(newline);
        var page = new RazorSourceFile(source, Path.GetFullPath("NestedGeneratedCalls.cshtml"));
        var entry = page.Analyze(-1).Nodes.Single(node => node.Label == "onclick → run()");
        var graph = page.Analyze(entry.TargetMethod!.Value);
        var outer = Condition(graph, "Model.Outer");
        var inner = Condition(graph, "Model.Inner");
        Assert.AreEqual(inner, Next(graph, Next(graph, outer, "True")));
        var end = Next(graph, outer, "False");
        Assert.AreEqual("Razor側の生成終了", end.Label);
        foreach (var (branch, name, line) in new[] { ("True", "success", 3), ("False", "failure", 4) })
        {
            var render = Next(graph, inner, branch);
            Assert.AreEqual(end, Next(graph, render));
            var call = Next(graph, render, "生成後、このJSが実行される場合");
            Assert.AreEqual(name + "()", Slice(source, call.Source!));
            Assert.AreEqual(page.FilePath, call.TargetFilePath);
            Assert.AreEqual(line, page.Entries.Single(method => method.Id == call.TargetMethod).NavigationSource.Line);
            Assert.AreEqual("JS断片の終了", Next(graph, call).Label);
        }
        Assert.DoesNotContain(node => node.Label == "合流" || node.Label.Contains("obj.focus()", StringComparison.Ordinal), graph.Nodes);
        Assert.Contains(node => node.Label == "done()", graph.Nodes);
    }

    [TestMethod]
    [DataRow("const value = ", ";")]
    [DataRow("load = alternative;", "done();")]
    public void ATextWrappedNestedRazorIfDoesNotInventAJumpWhenAdjacentOutputCanChangeTheCall(string before, string after)
    {
        var source = $$"""
            <script>
            function load() {}
            function run() {
                @if (Model.Outer) {
                    <text>
                        {{before}}
                        @if (Model.Inner) { <text>load();</text> }
                        {{after}}
                    </text>
                }
            }
            </script>
            """;
        var page = new RazorSourceFile(source, Path.GetFullPath("NestedAdjacentOutput.cshtml"));
        var graph = Analyze(page, "run");
        var render = Next(graph, Condition(graph, "Model.Inner"), "True");
        StringAssert.Contains(render.Label, "load()");
        Assert.DoesNotContain(edge => edge.From == render.Id && edge.Label == "生成後、このJSが実行される場合", graph.Edges);
    }

    [TestMethod]
    [DataRow("load()")]
    [DataRow("(load());")]
    public void StandaloneGeneratedCallNeedsASeparateStatementBoundary(string emitted)
    {
        var source = $$"""
            <script>
            start()
            @if (Model.Ready) {
                <text>{{emitted}}</text>
            }
            (finish());
            </script>
            """;
        var page = new RazorSourceFile(source, Path.GetFullPath("AsiBoundaries.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        var branch = Next(graph, Condition(graph, "Model.Ready"), "True");
        StringAssert.Contains(branch.Label, "load()");
        Assert.DoesNotContain(edge => edge.From == branch.Id && edge.Label == "生成後、このJSが実行される場合", graph.Edges);
    }
    [TestMethod]
    public void UnverifiedJavaScriptContextAndOversizedBranchesStayPreviews()
    {
        foreach (var (setup, finish, padding) in new[] { ("const markup = `", "`;", ""), ("", "", new string(' ', 10000)) })
        {
            var source = "<script>\n" + setup + "\n@if (Model.Ready) {\n    @:if (browser) {" + padding + " hit(); }\n}\n" + finish + "\n</script>";
            var page = new RazorSourceFile(source, Path.GetFullPath("Bounded.cshtml"));
            var graph = page.Analyze(-1);
            var branch = Next(graph, Condition(graph, "Model.Ready"), "True");
            Assert.IsLessThan(150, branch.Label.Length);
            StringAssert.Contains(branch.Label, "if (browser)");
            Assert.DoesNotContain(node => node.Kind == NodeKind.Condition && node.Label == "browser", graph.Nodes);
        }
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void TextBlockAndConsecutiveRazorLinesExposeTheGeneratedClientAlternatives(string newline)
    {
        var source = """
            <select onchange="reload()"></select>
            <script>
            function reload() {
                @if (Model.Ready) {
                    <text>
                        if (clientReady) { first(); } else { second(); }
                    </text>
                } else if (Model.Other) {
                    @:if (isOther) {
                    @:  third();
                    @:} else { fourth(); }
                }
                finish();
            }
            </script>
            """.ReplaceLineEndings(newline);
        var page = new RazorSourceFile(source, Path.GetFullPath("GeneratedBranches.cshtml"));
        var handler = page.Analyze(-1).Nodes.Single(node => node.Label == "onchange → reload()");
        Assert.IsNotNull(handler.TargetMethod);
        var graph = page.Analyze(handler.TargetMethod.Value);
        foreach (var (guard, predicate, yes, no, line) in new[]
        {
            ("Model.Ready", "clientReady", "first()", "second()", 6),
            ("Model.Other", "isOther", "third()", "fourth()", 9)
        })
        {
            var render = Next(graph, Condition(graph, guard), "True");
            StringAssert.Contains(render.Label, predicate);
            var client = Next(graph, render, "生成後、このJSが実行される場合");
            Assert.AreEqual(NodeKind.Condition, client.Kind);
            Assert.AreEqual(predicate, Slice(source, client.Source!));
            Assert.AreEqual(line, client.Source!.Line);
            var first = Next(graph, client, "True");
            var second = Next(graph, client, "False");
            Assert.AreEqual(yes, Slice(source, first.Source!));
            Assert.AreEqual(no, Slice(source, second.Source!));
            Assert.AreEqual(Next(graph, first), Next(graph, second));
            Assert.AreEqual("JS断片の終了", Next(graph, first).Label);
            Assert.AreEqual("Razor側の生成終了", Next(graph, render).Label);
        }
        Assert.AreEqual(1, graph.Nodes.Count(node => node.Kind == NodeKind.Start));
        Assert.Contains(node => node.Label == "finish()", graph.Nodes);
    }

    [TestMethod]
    [DataRow("", "", "apply()", true)]
    [DataRow("const other = input; window.status = 'ready';", "", "window.apply()", true)]
    [DataRow("", "let apply = input;", "apply()", false)]
    [DataRow("let apply = input;", "", "apply()", false)]
    [DataRow("function apply() {}", "", "apply()", false)]
    [DataRow("apply = input;", "", "apply()", false)]
    [DataRow("window.apply = input;", "", "window.apply()", false)]
    [DataRow("window[key] = input;", "", "apply()", false)]
    [DataRow("eval(input);", "", "apply()", false)]
    public void GeneratedRazorCallsDoNotInventTargetsWhenBindingsCanChange(
        string fragment, string outer, string call, bool follows)
    {
        var source = $$"""
            <script>
            function apply() { return 42; }
            function reload() {
                {{outer}}
                @if (Model.Ready) {
                    <text>{{fragment}} if (enabled) {{call}};</text>
                }
            }
            </script>
            """;
        var page = new RazorSourceFile(source, Path.GetFullPath("GeneratedBindings.cshtml"));
        var entry = page.Entries.Single(entry => entry.Name == "apply");
        var node = Analyze(page, "reload").Nodes.Single(node => node.Label == call);
        Assert.AreEqual(follows ? entry.Id : (int?)null, node.TargetMethod);
        Assert.AreEqual(follows ? page.FilePath : null, node.TargetFilePath);
        Assert.IsNull(node.TargetDeclaration);
    }

    [TestMethod]
    [DataRow("if (@Model.Value) ok();")]
    [DataRow("if (incomplete) {")]
    public void DynamicOrIncompleteOutputHasOnlyAPreview(string emitted)
    {
        var source = "<script>function reload() {\n@if (Model.Ready) {\n    @:" + emitted + "\n}\n}</script>";
        var page = new RazorSourceFile(source, Path.GetFullPath("NonStatic.cshtml"));
        var graph = Analyze(page, "reload");
        var branch = Next(graph, Condition(graph, "Model.Ready"), "True");
        StringAssert.Contains(branch.Label, "生成JS");
        Assert.DoesNotContain(edge => edge.From == branch.Id && edge.Label is not null, graph.Edges);
    }

    [TestMethod]
    [DataRow("\n", "")]
    [DataRow("\r\n", "")]
    [DataRow("\n", ";")]
    public void RazorBoundaryAfterTopLevelVariableStatementPreservesTheFollowingDefinition(string newline, string separator)
    {
        var source = $$"""
            <select onchange="reload();"></select>
            <script language="javascript">
            const value = prepareValue(){{separator}}
            @if (Model.Enabled)
            {
                <text>console.log(value);</text>
            }
            function reload() { return value; }
            </script>
            """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        var page = new RazorSourceFile(source, Path.GetFullPath("ScriptAsi.cshtml"));
        var target = Assert.ContainsSingle(page.Entries.Where(entry => entry.Name == "reload"));
        Assert.AreEqual(target.Id, page.Analyze(-1).Nodes.Single(node => node.Label == "onchange → reload()").TargetMethod);
        var inline = page.Entries.Single(entry => page.IsInlineEvent(entry.Id));
        Assert.AreEqual(target.Id, page.Analyze(inline.Id).Nodes.Single(node => node.Label == "reload()").TargetMethod);
        var init = Analyze(page, "JavaScript 初期化");
        var initializer = Assert.ContainsSingle(init.Nodes.Where(node => node.Label == "prepareValue()"));
        Assert.AreEqual(source.IndexOf("prepareValue()", StringComparison.Ordinal), initializer.Source!.Offset);
        var boundary = Next(init, initializer);
        Assert.IsNotNull(boundary.Source);
        Assert.AreEqual(source.IndexOf("if (Model.Enabled)", StringComparison.Ordinal), boundary.Source.Offset);
        Assert.AreEqual(4, boundary.Source.Line);
        Assert.AreEqual("Model.Enabled", Slice(source, Condition(init, "Model.Enabled").Source!));
        var guardedOutput = Next(init, Condition(init, "Model.Enabled"), "True");
        var emitted = Next(init, guardedOutput, "生成後、このJSが実行される場合");
        Assert.AreEqual("console.log(value)", emitted.Label);
        Assert.AreEqual("JS断片の終了", Next(init, emitted).Label);
        Assert.IsTrue(init.IsPartial);
        Assert.DoesNotContain(notice => notice.Contains("独立したJavaScript文として確定できません", StringComparison.Ordinal), page.Notices);
        var returned = Assert.ContainsSingle(page.Analyze(target.Id).Nodes.Where(node => node.Label == "return value;"));
        Assert.AreEqual(source.IndexOf("return value;", StringComparison.Ordinal), returned.Source!.Offset);
        Assert.AreEqual(8, returned.Source.Line);
    }

    [TestMethod]
    public void ReturnSlotFailureOffersOnlyTheIntactDefinitionSource()
    {
        var page = FailedReturnPage("function reload() { return 3; }");
        var expected = page.Text.IndexOf("function reload()", StringComparison.Ordinal);
        foreach (var node in new[] { page.Analyze(-1).Nodes.Single(node => node.Label == "onchange → reload()"),
            page.Analyze(page.Entries.Single(entry => page.IsInlineEvent(entry.Id)).Id).Nodes.Single(node => node.Label == "reload()") })
        {
            Assert.IsNull(node.TargetMethod);
            Assert.IsNotNull(node.TargetDeclaration);
            Assert.IsTrue(node.IsDefinitionCandidate);
            Assert.AreEqual(page.FilePath, node.TargetDeclaration.FilePath);
            Assert.AreEqual(expected, node.TargetDeclaration.Offset);
            Assert.AreEqual("function reload() { return 3; }", Slice(page.Text, node.TargetDeclaration));
        }
        var init = Analyze(page, "JavaScript 初期化");
        Assert.IsTrue(init.IsPartial);
        Condition(init, "Model.Enabled");
        Assert.DoesNotContain(node => node.Kind == NodeKind.Transfer && !IsRender(node.Id), init.Nodes);
        Assert.Contains(notice => notice.Contains("独立したJavaScript文として確定できません", StringComparison.Ordinal), init.Notices);
    }

    [TestMethod]
    [DataRow("function reload() {} function reload() {}")]
    [DataRow("function outer() { function reload() {} }")]
    [DataRow("""const value = "function reload() {}";""")]
    [DataRow("function reload() {} window.reload = unknown;")]
    [DataRow("function reload() {} function modify() { eval(code); }")]
    [DataRow("function reload() {}</script><script>function reload() {}")]
    [DataRow("function reload() {} function modify() { eval(code); }</script><script>function reload() {}")]
    public void ReturnSlotFailureDoesNotGuessACompetingOrNonVisibleDefinition(string after)
    {
        var page = FailedReturnPage(after);
        var node = page.Analyze(-1).Nodes.Single(node => node.Label == "onchange → reload()");
        Assert.IsNull(node.TargetMethod);
        Assert.IsNull(node.TargetDeclaration);
    }

    private static RazorSourceFile FailedReturnPage(string after) => new($$"""
        <select onchange="reload();"></select>
        <script language="javascript">
          function other() {
              return @if (Model.Enabled)
              {
                  <text>1;</text>
              }
              else
              {
                  <text>2;</text>
              }
          }
          {{after}}
        </script>
        """, Path.GetFullPath("FailedReturnSlot.cshtml"));

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void JavaScriptFunctionKeepsGeneratedCallsSeparateFromOrdinaryBrowserFlow(string newline)
    {
        var source = """
            @page
            <script>
            @if (Model.ScriptReady)
            {
                @:init();
            }
            function update() {
                before();
                @if (Model.Enabled)
                {
                    @:enable();
                }
                else
                {
                    <text>disable();</text>
                }
                @for (var index = 0; index < Model.Total; index++)
                {
                    <text>append(@index);</text>
                }
                if (window.confirmed) browserAction();
                after();
            }
            function sibling() {
                @while (Model.SiblingReady)
                {
                    @:siblingAction();
                }
            }
            </script>
            """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        var path = Path.GetFullPath("ScriptControls.cshtml");
        var page = new RazorSourceFile(source, path);
        var graph = Analyze(page, "update");
        var entry = Assert.ContainsSingle(graph.Nodes.Where(node => node.Kind == NodeKind.Start));
        Assert.AreEqual("update", entry.Label);
        var enabled = Condition(graph, "Model.Enabled");
        Assert.AreEqual(enabled, Next(graph, entry, "サーバー側の生成"));
        Assert.AreEqual("before()", Next(graph, entry, "ブラウザー側の実行候補").Label);
        var diagram = MermaidWriter.Write(graph);
        StringAssert.Contains(diagram, "サーバー側の生成");
        StringAssert.Contains(diagram, "ブラウザー側の実行候補");
        Assert.IsFalse(diagram.Contains("Razor · JavaScriptの生成", StringComparison.Ordinal));
        var loop = Condition(graph, "index < Model.Total");
        Assert.IsNotNull(enabled.Source);
        Assert.AreEqual(path, enabled.Source.FilePath);
        Assert.AreEqual(9, enabled.Source.Line);
        Assert.AreEqual("Model.Enabled", Slice(source, enabled.Source));

        var yes = Next(graph, enabled, "True");
        var no = Next(graph, enabled, "False");
        Assert.IsNotNull(yes.Source);
        Assert.IsNotNull(no.Source);
        StringAssert.Contains(Slice(source, yes.Source), "enable();");
        StringAssert.Contains(Slice(source, no.Source), "disable();");
        Assert.AreEqual(loop, Next(graph, yes));
        Assert.AreEqual(loop, Next(graph, no));
        Assert.AreEqual(loop, Next(graph, Next(graph, loop, "True")));
        Assert.AreEqual(NodeKind.End, Next(graph, loop, "False").Kind);

        Assert.IsTrue(Condition(graph, "window.confirmed").IsReachable);
        Assert.Contains(node => node.Label == "before()", graph.Nodes);
        Assert.Contains(node => node.Label == "after()", graph.Nodes);
        Assert.AreEqual(2, graph.Nodes.Count(node => node.Label.Contains("実行経路未解決", StringComparison.Ordinal)));
        foreach (var (branch, name) in new[] { (yes, "enable()"), (no, "disable()") })
        {
            var call = Next(graph, branch, "生成後、このJSが実行される場合");
            Assert.AreEqual(name, call.Label);
            Assert.AreEqual("JS断片の終了", Next(graph, call).Label);
        }
        Assert.DoesNotContain(node => node.Label == "append(@index)" || (node.Label is "enable()" or "disable()") && !IsRender(node.Id), graph.Nodes);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Condition && node.Label.Contains("SiblingReady", StringComparison.Ordinal), graph.Nodes);
        Assert.DoesNotContain(edge => edge.From != entry.Id && IsRender(edge.From) != IsRender(edge.To), graph.Edges);
        Assert.DoesNotContain(notice => notice.Contains("JavaScript構文エラー", StringComparison.Ordinal), graph.Notices);

        var initial = Analyze(page, "JavaScript 初期化");
        var start = Assert.ContainsSingle(initial.Nodes.Where(node => node.Kind == NodeKind.Start));
        StringAssert.StartsWith(start.Label, "JavaScript 初期化");
        Assert.AreEqual(Condition(initial, "Model.ScriptReady"), Next(initial, start, "サーバー側の生成"));
        Assert.IsFalse(IsRender(Next(initial, start, "ブラウザー側の実行候補").Id));
        Assert.DoesNotContain(edge => edge.From != start.Id && IsRender(edge.From) != IsRender(edge.To), initial.Edges);
        Assert.AreEqual(1, initial.Nodes.Count(node => node.Kind == NodeKind.Condition && IsRender(node.Id)));
        Condition(Analyze(page, "sibling"), "Model.SiblingReady");
        Condition(page.Analyze(-1), "Model.Enabled");
    }

    [TestMethod]
    public void JQueryReadyBodyHasASeparateDeferredBranchAndPreservesRazorGeneration()
    {
        const string source = """
            <script>
            before();
            $(function () {
                @if (Model.Server)
                {
                    <text>generatedStep();</text>
                }
                if (browserReady) {
                    run();
                    return;
                }
                fallback();
            });
            after();
            function run() {}
            function fallback() {}
            function generatedStep() {}
            function custom($) {
                $(function () { unrelated(); });
            }
            </script>
            <script>jQuery(() => announce());</script>
            """;
        var page = new RazorSourceFile(source, Path.GetFullPath("JQueryReady.cshtml"));
        var initializers = page.Entries.Where(entry => entry.Name.StartsWith("JavaScript 初期化 ", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(2, initializers);
        var flow = page.Analyze(initializers[0].Id);
        var start = Assert.ContainsSingle(flow.Nodes.Where(node => node.Kind == NodeKind.Start));
        var generate = Condition(flow, "Model.Server");
        Assert.AreEqual(generate, Next(flow, start, "サーバー側の生成"));
        Assert.AreEqual("generatedStep()", Next(flow, Next(flow, generate, "True"), "生成後、このJSが実行される場合").Label);
        var before = Next(flow, start, "ブラウザー側の実行候補");
        Assert.AreEqual("before()", before.Label);
        var registration = Next(flow, before);
        Assert.AreEqual("jQuery ready を登録", registration.Label);
        var ready = Condition(flow, "browserReady");
        Assert.AreEqual(NodeKind.Condition, ready.Kind);
        Assert.AreEqual("after()", Next(flow, registration).Label);
        Assert.Contains(edge => edge.From == registration.Id && edge.Label == "DOM 準備完了時", flow.Edges);
        var run = Next(flow, ready, "True");
        Assert.AreEqual("run()", run.Label);
        Assert.AreEqual("run()", Slice(source, run.Source!));
        Assert.AreEqual(page.Entries.Single(entry => entry.Name == "run").Id, run.TargetMethod);
        Assert.AreEqual("ready コールバック終了", Next(flow, Next(flow, run)).Label);
        Assert.AreEqual("fallback()", Next(flow, ready, "False").Label);
        Assert.AreEqual(page.Entries.Single(entry => entry.Name.StartsWith("無名関数 L3", StringComparison.Ordinal)).Id,
            registration.TargetMethod);
        Assert.DoesNotContain(node => node.Label == "unrelated()", flow.Nodes);

        var custom = Analyze(page, "custom");
        Assert.DoesNotContain(edge => edge.Label == "DOM 準備完了時", custom.Edges);
        Assert.Contains(node => node.Kind == NodeKind.Call && node.Label.StartsWith("$(function", StringComparison.Ordinal), custom.Nodes);
        var other = page.Analyze(initializers[1].Id);
        Assert.AreEqual("announce()", Next(other, other.Nodes.Single(node => node.Label == "jQuery ready を登録"), "DOM 準備完了時").Label);
        Assert.DoesNotContain(node => node.Label == "run()", other.Nodes);
    }

    [TestMethod]
    public void LargeReadyBodyKeepsTheRegistrationAndSurroundingInitialization()
    {
        var script = "before();\n$(function() {\n" + string.Concat(Enumerable.Repeat("work();\n", 205))
            + "});\nafter();";
        var path = Path.GetFullPath("ReadyLimit.js");
        var js = new RazorJavaScript(script, path, [new RazorJavaScriptFragment(script, new SourceRange(0, script.Length, 1, path))]);
        var flow = js.Analyze(js.Entries[0].Id);
        var before = Next(flow, Assert.ContainsSingle(flow.Nodes.Where(node => node.Kind == NodeKind.Start)));
        var ready = Next(flow, before);
        Assert.AreEqual("before()", before.Label);
        Assert.AreEqual("jQuery ready を登録", ready.Label);
        Assert.AreEqual("after()", Next(flow, ready).Label);
        Assert.AreEqual(4, flow.Edges.Count);
        Assert.IsTrue(flow.IsPartial);
        Assert.IsNotNull(ready.TargetMethod);
        Assert.AreEqual(205, js.Analyze(ready.TargetMethod.Value).Nodes.Count(node => node.Label == "work()"));
    }

    [TestMethod]
    public void SameNamedFunctionsKeepSeparateDefinitionsAndRazorControls()
    {
        var page = new RazorSourceFile("""
            <select onchange="reload();"></select>
            <script>
            function reload() {
                @if (Model.GlobalReady)
                {
                    @:loadGlobal();
                }
                return 1;
            }
            function setup() {
                function reload() {
                    @if (Model.LocalReady)
                    {
                        @:loadLocal();
                    }
                    return 2;
                }
                return reload();
            }
            </script>
            """, Path.GetFullPath("ScopedScripts.cshtml"));
        var declared = page.Entries.Where(entry => entry.Name == "reload").OrderBy(entry => entry.Source.Offset).ToArray();
        Assert.HasCount(2, declared);
        var overview = page.Analyze(-1);
        var inline = page.Entries.Single(entry => page.IsInlineEvent(entry.Id));
        Assert.AreEqual(declared[0].Id, overview.Nodes.Single(node => node.Label == "onchange → reload()").TargetMethod);
        Assert.AreEqual(declared[0].Id, page.Analyze(inline.Id).Nodes.Single(node => node.Label == "reload()").TargetMethod);
        var setup = Analyze(page, "setup");
        Assert.AreEqual(declared[1].Id, setup.Nodes.Single(node => node.Label == "reload()").TargetMethod);
        Assert.DoesNotContain(node => IsRender(node.Id), setup.Nodes);

        foreach (var (declaration, own, other) in new[]
        {
            (declared[0], "Model.GlobalReady", "Model.LocalReady"),
            (declared[1], "Model.LocalReady", "Model.GlobalReady"),
        })
        {
            var graph = page.Analyze(declaration.Id);
            var entry = Assert.ContainsSingle(graph.Nodes.Where(node => node.Kind == NodeKind.Start));
            Assert.AreEqual(declaration.NavigationSource, entry.Source);
            var condition = Condition(graph, own);
            Assert.AreEqual(condition, Next(graph, entry, "サーバー側の生成"));
            Assert.DoesNotContain(node => node.Label.Contains(other, StringComparison.Ordinal), graph.Nodes);
        }
    }

    [TestMethod]
    public void UnparseableBrowserProjectionKeepsRazorScriptEntryAndItsBothPaths()
    {
        var page = new RazorSourceFile("""
            @page
            <script>
            let value =
                @if (Model.Enabled)
                {
                    <text>1</text>
                }
                else
                {
                    <text>0</text>
                }
                ;
            </script>
            """, Path.GetFullPath("ScriptExpression.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        var condition = Condition(graph, "Model.Enabled");
        var start = Assert.ContainsSingle(graph.Nodes.Where(node => node.Kind == NodeKind.Start));
        Assert.AreEqual(condition, Next(graph, start, "サーバー側の生成"));
        Assert.AreEqual(Next(graph, Next(graph, condition, "True")), Next(graph, Next(graph, condition, "False")));
        Assert.IsTrue(graph.IsPartial);
        var browser = Next(graph, start, "ブラウザー側の実行候補");
        Assert.AreEqual(NodeKind.Exception, browser.Kind);
        Assert.IsFalse(IsRender(browser.Id));
        Assert.Contains(notice => notice.Contains("JavaScript構文エラー", StringComparison.Ordinal), graph.Notices);
        Assert.DoesNotContain(edge => edge.From != start.Id && IsRender(edge.From) != IsRender(edge.To), graph.Edges);
    }

    [TestMethod]
    public void RazorSlotConsumedAsReturnTerminatorDoesNotInventVoidReturn()
    {
        var page = new RazorSourceFile("""
            @page
            <script>
            function selected() {
                return @if (Model.Enabled)
                {
                    <text>1;</text>
                }
                else
                {
                    <text>2;</text>
                }
            }
            </script>
            """, Path.GetFullPath("ScriptReturnExpression.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        Condition(graph, "Model.Enabled");
        Assert.DoesNotContain(node => node.Kind == NodeKind.Transfer && !IsRender(node.Id), graph.Nodes);
        Assert.Contains(notice => notice.Contains("独立したJavaScript文として確定できません", StringComparison.Ordinal), graph.Notices);
    }

    [TestMethod]
    public void StandaloneRazorSlotDoesNotTurnFollowingJsIntoUnbracedIfBody()
    {
        var page = new RazorSourceFile("""
            @page
            <script>
            if (browserReady)
                @if (Model.Enabled)
                {
                    @:emit();
                }
            tail();
            </script>
            """, Path.GetFullPath("UnbracedBrowserIf.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        var browserIf = Condition(graph, "browserReady");
        var boundary = Next(graph, browserIf, "True");
        Assert.AreEqual("tail()", Next(graph, browserIf, "False").Label);
        Assert.AreEqual(NodeKind.Exception, boundary.Kind);
        Assert.AreEqual("tail()", Next(graph, boundary, "続行する場合").Label);
        var branch = Next(graph, Condition(graph, "Model.Enabled"), "True");
        var emitted = Next(graph, branch, "生成後、このJSが実行される場合");
        Assert.AreEqual("emit()", emitted.Label);
        Assert.DoesNotContain(edge => edge.To == emitted.Id && edge.From != branch.Id, graph.Edges);
        Assert.AreEqual("JS断片の終了", Next(graph, emitted).Label);
    }

    [TestMethod]
    public void RazorOutsideScriptDoesNotEraseBrowserFunctions()
    {
        var page = new RazorSourceFile("""
            @page
            @if (Model.ShowScript)
            {
                <script>
                function run() {
                    if (window.ready) execute();
                }
                </script>
            }
            """, Path.GetFullPath("GuardedScript.cshtml"));
        var graph = Analyze(page, "run");
        Condition(graph, "window.ready");
        Assert.Contains(node => node.Label == "execute()", graph.Nodes);
        Assert.DoesNotContain(node => IsRender(node.Id), graph.Nodes);
        Condition(page.Analyze(-1), "Model.ShowScript");
    }

    [TestMethod]
    public void UnsupportedRazorTryDoesNotPromoteNestedIfOrFlattenCatch()
    {
        var page = new RazorSourceFile("""
            @page
            <script>
            @try
            {
                if (Model.Enabled)
                {
                    @:whenEnabled();
                }
            }
            catch
            {
                @:recover();
            }
            tail();
            </script>
            """, Path.GetFullPath("ScriptTry.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        Assert.Contains(node => IsRender(node.Id) && node.Label.Contains("try / catch", StringComparison.Ordinal), graph.Nodes);
        Assert.DoesNotContain(node => node.Kind == NodeKind.Condition && IsRender(node.Id), graph.Nodes);
        Assert.DoesNotContain(node => node.Label is "whenEnabled()" or "recover()", graph.Nodes);
        Assert.Contains(node => node.Label == "tail()", graph.Nodes);
    }

    [TestMethod]
    public void RenderingDisplayLimitDoesNotLeakBranchOutputIntoBrowserFlow()
    {
        var controls = string.Join("\n", Enumerable.Range(0, 101).Select(i => $$"""
            @if (Model.Check({{i}}))
            {
                @:onlyCandidate{{i}}();
            }
            """));
        var page = new RazorSourceFile($"@page\n<script>\n{controls}\n tail();\n</script>",
            Path.GetFullPath("LargeMixedScript.cshtml"));
        var graph = Analyze(page, "JavaScript 初期化");
        Assert.AreEqual(100, graph.Nodes.Count(node => node.Kind == NodeKind.Condition && IsRender(node.Id)));
        Assert.AreEqual(101, graph.Nodes.Count(node => node.Label.Contains("実行経路未解決", StringComparison.Ordinal)));
        var generated = graph.Nodes.Where(node => node.Label.StartsWith("onlyCandidate", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(generated.Length is > 0 and <= 66);
        Assert.IsTrue(generated.All(node => IsRender(node.Id)));
        Assert.Contains(node => node.Label == "tail()", graph.Nodes);
    }

    private static FlowDiagram Analyze(RazorSourceFile page, string name) =>
        page.Analyze(page.Entries.Single(entry => entry.Name.StartsWith(name, StringComparison.Ordinal)).Id);
    private static FlowNode Condition(FlowDiagram graph, string text) =>
        graph.Nodes.Single(node => node.Kind == NodeKind.Condition && node.Label.Contains(text, StringComparison.Ordinal));
    private static FlowNode Next(FlowDiagram graph, FlowNode node, string? label = null) =>
        graph.Nodes.Single(target => target.Id == graph.Edges.Single(edge => edge.From == node.Id && edge.Label == label).To);
    private static string Slice(string source, SourceRange range) => source.Substring(range.Offset, range.Length);
    private static bool IsRender(string id) => id.StartsWith("render_", StringComparison.Ordinal);
}
