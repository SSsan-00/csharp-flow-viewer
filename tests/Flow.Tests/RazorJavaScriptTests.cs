using Flow.Analysis;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class RazorJavaScriptTests
{
    private const string PagePath = "/Pages/Order.cshtml";

    private static RazorJavaScript Parse(string code, string prefix = "") =>
        new(prefix + code, PagePath, [new RazorJavaScriptFragment(code,
            new SourceRange(prefix.Length, code.Length, 1 + prefix.Count(ch => ch == '\n'), PagePath))]);

    [TestMethod]
    public void ChangeRegistrationsIncludeNativeJQueryAndLegacyForms()
    {
        var source = Parse("""
            function changed(e) {
                e = e || window.event;
                if (e.srcElement) report(e.srcElement.value);
            }
            var input = document.getElementById("category");
            input.onchange = changed;
            input.addEventListener("change", changed);
            $("#category").on("change", changed);
            $(document).on("change", "#category", changed);
            $("#category").change(changed);
            $("#category").bind("change", changed);
            $(document).delegate("#category", "change", changed);
            $("#category").live("change", changed);
            input.attachEvent("onchange", changed);
            """);
        var handler = Assert.ContainsSingle(source.Entries.Where(entry => entry.Name == "changed"));
        Assert.AreEqual(9, source.Bindings.Count);
        Assert.IsTrue(source.Bindings.All(binding => binding.EventName == "change"
            && binding.Selector == "#category" && binding.HandlerEntryId == handler.Id));
        Assert.AreEqual(3, source.Bindings.Count(binding => binding.IsDelegated));
        Assert.AreEqual(2, source.Bindings.Count(binding => binding.IsLegacy));
    }

    [TestMethod]
    public void InlineChangeFindsNamedFunctionAndKeepsOriginalSourceLocations()
    {
        const string script = "function changeCategory(value) { if (value) save(value); else return false; }";
        const string prefix = "@page\r\n<p>日本語の画面</p>\r\n<script>";
        var source = Parse(script, prefix);
        const string code = "changeCategory(this.value);";
        var inline = source.AddInlineHandler("分類 change", code,
            new SourceRange(0, code.Length, 1, PagePath));
        var target = Assert.ContainsSingle(source.Entries.Where(entry => entry.Name == "changeCategory"));
        var caller = Assert.ContainsSingle(source.Analyze(inline).Nodes.Where(node => node.TargetMethod == target.Id));
        Assert.AreEqual(PagePath, caller.TargetFilePath);
        var graph = source.Analyze(target.Id);
        Assert.Contains(node => node.Kind == NodeKind.Condition && node.Label == "value", graph.Nodes);
        Assert.Contains(node => node.Label.StartsWith("return false", StringComparison.Ordinal), graph.Nodes);
        Assert.IsTrue(graph.Nodes.Where(node => node.Source is not null).All(node =>
            node.Source!.Offset >= prefix.Length && node.Source.Offset + node.Source.Length <= prefix.Length + script.Length
            && node.Source.FilePath == PagePath && node.Source.Line == 3));
    }

    [TestMethod]
    public void JavaScriptStringsCommentsAndShadowedNamesDoNotCreateFalseBindingsOrTargets()
    {
        var source = Parse("""
            function save() {}
            var text = 'document.getElementById("fake").onchange = save';
            // $("#fake").on("change", save);
            function local(save, document, $) {
                save();
                document.getElementById("fake").onchange = save;
                $("#fake").change(save);
            }
            """);
        var local = Assert.ContainsSingle(source.Entries.Where(entry => entry.Name == "local"));
        var graph = source.Analyze(local.Id);
        Assert.DoesNotContain(node => node.TargetMethod is not null, graph.Nodes);
        Assert.DoesNotContain(binding => binding.Selector == "#fake", source.Bindings);
        Assert.Contains(binding => binding.Selector == "動的な要素" && binding.HandlerEntryId is null, source.Bindings);
    }

    [TestMethod]
    public void XhrSendPreservesRequestDetailsAndDoesNotExecuteResponseCallbacksSynchronously()
    {
        var source = Parse("""
            function sendOrder(payload) {
                var xhr = new XMLHttpRequest();
                xhr.open("POST", "/Order?handler=Save", true);
                xhr.onreadystatechange = function () {
                    if (xhr.readyState === 4) finish(xhr.responseText);
                };
                xhr.addEventListener("error", function () { showError(); });
                xhr.send(payload);
                afterSend();
            }
            """);
        var request = Assert.ContainsSingle(source.Requests);
        Assert.AreEqual("POST", request.HttpMethod);
        Assert.AreEqual("/Order?handler=Save", request.Url);
        Assert.AreEqual("payload", request.Body);
        Assert.IsTrue(request.IsAsync);
        Assert.IsTrue(request.IsResolved);
        Assert.AreEqual(2, request.CallbackEntryIds.Count);
        var graph = source.Analyze(request.OwnerEntryId);
        Assert.Contains(node => node.Label.StartsWith("HTTP POST /Order?handler=Save", StringComparison.Ordinal), graph.Nodes);
        Assert.DoesNotContain(node => node.Label is "finish(xhr.responseText)" or "showError()", graph.Nodes);
        Assert.IsTrue(request.CallbackEntryIds.Select(id => source.Analyze(id))
            .SelectMany(diagram => diagram.Nodes).Any(node => node.Label == "finish(xhr.responseText)"));
    }

    [TestMethod]
    public void XhrCallbacksRemainDeduplicatedAndOrderedAcrossScriptsAndSends()
    {
        const string first = """
            function first() { return 1; }
            function second() { return 2; }
            var xhr = new XMLHttpRequest();
            xhr.open("GET", "/Orders");
            xhr.send();
            """;
        const string second = """
            xhr.onload = first;
            xhr.addEventListener("load", first);
            xhr.onerror = second;
            xhr.send();
            """;
        var source = new RazorJavaScript(first + "\n" + second, PagePath,
        [
            new(first, new SourceRange(0, first.Length, 1, PagePath)),
            new(second, new SourceRange(first.Length + 1, second.Length, 6, PagePath))
        ]);
        Assert.HasCount(2, source.Requests);
        var expected = new[] { "first", "second" }.Select(name => source.Entries.Single(entry => entry.Name == name).Id).ToArray();
        foreach (var request in source.Requests)
            CollectionAssert.AreEqual(expected, request.CallbackEntryIds.ToArray());
        Assert.HasCount(3, source.Bindings);
    }

    [TestMethod]
    public void XhrInstancesWithSameVariableNameDoNotShareCallbacksOrOpenConfiguration()
    {
        var source = Parse("""
            function firstCallback() {}
            function secondCallback() {}
            function first() {
                var xhr = new XMLHttpRequest();
                xhr.open("GET", "/First");
                xhr.onload = firstCallback;
                xhr.send();
            }
            function second() {
                var xhr = new XMLHttpRequest();
                xhr.onload = secondCallback;
                xhr.send();
            }
            """);
        var first = Assert.ContainsSingle(source.Requests.Where(request => request.Url == "/First"));
        var second = Assert.ContainsSingle(source.Requests.Where(request => request != first));
        Assert.IsTrue(first.IsResolved);
        Assert.IsFalse(second.IsResolved);
        var firstCallback = source.Entries.Single(entry => entry.Name == "firstCallback");
        var secondCallback = source.Entries.Single(entry => entry.Name == "secondCallback");
        CollectionAssert.AreEqual(new[] { firstCallback.Id }, first.CallbackEntryIds.ToArray());
        CollectionAssert.AreEqual(new[] { secondCallback.Id }, second.CallbackEntryIds.ToArray());
    }

    [TestMethod]
    public void ConditionalOpenAndAnotherFunctionsOpenDoNotProveTheSendDestination()
    {
        var source = Parse("""
            var xhr = new XMLHttpRequest();
            function configure() { xhr.open("POST", "/NeverCalled"); }
            function sendUnknown() { xhr.send(); }
            function sendConditional(flag) {
                var other = new XMLHttpRequest();
                if (flag) other.open("GET", "/Optional");
                other.send();
            }
            """);
        Assert.AreEqual(2, source.Requests.Count);
        Assert.IsTrue(source.Requests.All(request => !request.IsResolved));
        Assert.DoesNotContain(request => request.Url == "/NeverCalled", source.Requests);
    }

    [TestMethod]
    public void RazorProjectionRetainsStaticFunctionsButMarksGeneratedValuesAsUnresolved()
    {
        const string original = "function changed() { var id = @Model.Id; send(id); }";
        var projected = original.Replace("@Model.Id", "0        ", StringComparison.Ordinal);
        Assert.AreEqual(original.Length, projected.Length);
        var source = new RazorJavaScript(original, PagePath,
            [new RazorJavaScriptFragment(projected, new SourceRange(0, original.Length, 1, PagePath), ContainsRazorCode: true)]);
        var handler = Assert.ContainsSingle(source.Entries.Where(entry => entry.Name == "changed"));
        var graph = source.Analyze(handler.Id);
        Assert.IsTrue(graph.IsPartial);
        Assert.Contains(node => node.Label == "send(id)", graph.Nodes);
        Assert.Contains(node => node.Label.Contains("@Model.Id", StringComparison.Ordinal), graph.Nodes);
    }

    [TestMethod]
    public void MalformedJavaScriptReportsPartialAnalysisAndDoesNotInventFunctions()
    {
        var source = Parse("function incomplete( { ");
        Assert.IsEmpty(source.Entries);
        Assert.Contains(message => message.StartsWith("解析は部分的", StringComparison.Ordinal), source.Notices);
    }

    [TestMethod]
    public void ConstructorAssignmentsAndLegacyFallbackRemainVisibleButUncertain()
    {
        var source = Parse("""
            function sendOld(flag) {
                var xhr;
                if (flag) xhr = new XMLHttpRequest();
                else xhr = new ActiveXObject("Microsoft.XMLHTTP");
                xhr.open("GET", "/Old");
                xhr.send();
            }
            """);
        var request = Assert.ContainsSingle(source.Requests);
        Assert.AreEqual("/Old", request.Url);
        Assert.IsFalse(request.IsResolved);
        Assert.Contains(message => message.Contains("ActiveXObject", StringComparison.Ordinal), source.Notices);
    }

    [TestMethod]
    public void ReassignmentThroughAClosureDoesNotKeepTheOriginalFunctionTarget()
    {
        var source = Parse("""
            function save() {}
            function replace() { save = dynamicHandler; }
            function run() { replace(); save(); }
            """);
        var run = source.Entries.Single(item => item.Name == "run");
        var call = source.Analyze(run.Id).Nodes.Single(node => node.Label == "save()");
        Assert.IsNull(call.TargetMethod);
    }

    [TestMethod]
    public void ReturnFalseTerminatesItsBranchBeforeTheSend()
    {
        var source = Parse("function submitOrder(ok) { if (!ok) return false; form.submit(); }");
        var entry = source.Entries.Single(item => item.Name == "submitOrder");
        var graph = source.Analyze(entry.Id);
        var returning = graph.Nodes.Single(node => node.Label.StartsWith("return false", StringComparison.Ordinal));
        var successor = graph.Nodes.Single(node => node.Id == graph.Edges.Single(edge => edge.From == returning.Id).To);
        Assert.AreEqual(NodeKind.End, successor.Kind);
        Assert.Contains(node => node.Label == "form.submit()", graph.Nodes);
    }
}
