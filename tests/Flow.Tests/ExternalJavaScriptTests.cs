using Flow.Analysis;
using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class ExternalJavaScriptTests
{
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
