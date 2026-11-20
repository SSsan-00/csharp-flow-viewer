using Flow.Analysis;
using Flow.Mermaid;
using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class AnalysisTests(TestContext testContext)
{
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Where(path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll"
            or "System.Collections.dll" or "System.Linq.dll" or "Microsoft.CSharp.dll" or "System.Linq.Expressions.dll")
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    internal static SourceFile File(string source, string? other = null, bool allowErrors = false)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var trees = other is null ? new[] { tree } : [tree, CSharpSyntaxTree.ParseText(other)];
        var compilation = CSharpCompilation.Create("Fictional", trees, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        if (!allowErrors)
            Assert.IsEmpty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return new SourceFile(compilation.GetSemanticModel(tree));
    }

    private static FlowDiagram Diagram(SourceFile file, string method = "Run") =>
        file.Analyze(Assert.ContainsSingle(file.Methods.Where(item => item.Name == method)).Id);

    private static FlowNode Single(FlowDiagram diagram, string label) =>
        Assert.ContainsSingle(diagram.Nodes.Where(node => node.Label == label));

    private static FlowNode Next(FlowDiagram diagram, FlowNode from, string? branch = null) =>
        diagram.Nodes.Single(node => node.Id == Assert.ContainsSingle(diagram.Edges.Where(edge =>
            edge.From == from.Id && edge.Label == branch && edge.IsReachable)).To);

    private static IEnumerable<FlowNode> PathFrom(FlowDiagram diagram, FlowNode from)
    {
        var seen = new HashSet<string>();
        while (seen.Add(from.Id))
        {
            yield return from;
            var successors = diagram.Edges.Where(edge => edge.From == from.Id && edge.IsReachable).ToArray();
            if (successors.Length != 1)
                yield break;
            from = diagram.Nodes.Single(node => node.Id == successors[0].To);
        }
    }

    [TestMethod]
    public void SealedReceiverLinksToInheritedVirtualImplementationAndBaseCallsRemainDirect()
    {
        var file = File("""
            class Base
            {
                public virtual void Work() {}
            }
            class Middle : Base
            {
                public override void Work() {}
            }
            sealed class Child : Base {}
            sealed class Grandchild : Middle {}
            sealed class Own : Base
            {
                public override void Work() {}
                public void RunBase() => base.Work();
            }
            class Demo
            {
                void Run(Child child, Grandchild grandchild, Own own, Base unknown)
                {
                    child.Work();
                    grandchild.Work();
                    own.Work();
                    unknown.Work();
                }
            }
            """);
        var diagram = Diagram(file);
        foreach (var type in new[] { "Base", "Middle", "Own" })
        {
            var target = Assert.ContainsSingle(file.Methods.Where(entry => entry.Signature == type + ".Work()"));
            Assert.AreEqual(target.Id, Single(diagram, type + ".Work()").TargetMethod);
        }
        Assert.IsNull(Single(diagram, "Base.Work() · 実行時に決定").TargetMethod);
        Assert.AreEqual(Single(diagram, "Base.Work()").TargetMethod,
            Single(Diagram(file, "RunBase"), "Base.Work()").TargetMethod);
    }

    [TestMethod]
    public void ConditionAndSwitchLabelsPreserveMeaningfulLiteralWhitespace()
    {
        var text = """"
            class Demo
            {
                bool Run(string value, string name)
                {
                    if (value   == "A  B") return true;
                    if (value == @"A  B") return true;
                    if (value == """A  B""") return true;
                    if (value == $"A  {name}") return true;
                    if (value == @"A<TAB>B
                        C") return true;
                    return false;
                }
                int Choose(string value) => value switch { "A  B" => 1, "A B" => 2, _ => 3 };
            }
            """".Replace("<TAB>", "\t", StringComparison.Ordinal);
        var file = File(text);
        var diagram = Diagram(file);
        foreach (var label in new[] { "value == \"A  B\"", "value == @\"A  B\"", "value == \"\"\"A  B\"\"\"", "value == $\"A  {name}\"" })
            Assert.AreEqual(NodeKind.Condition, Single(diagram, label).Kind);
        Assert.Contains(node => node.Kind == NodeKind.Condition && node.Label.Contains("A␉B", StringComparison.Ordinal)
            && node.Label.Contains('␊'), diagram.Nodes);
        var choose = Diagram(file, "Choose");
        Assert.IsNotNull(Single(choose, "case \"A  B\" に一致"));
        Assert.IsNotNull(Single(choose, "case \"A B\" に一致"));
        var mermaid = MermaidWriter.Write(choose, includeSourceLines: false);
        StringAssert.Contains(mermaid, "A #160;B");
    }

    [TestMethod]
    public void CrossFileCallsUseOriginalOffsetsAndOnlyNavigateToProjectFilesWithBodies()
    {
        var directory = Path.GetFullPath("FictionalProject");
        var callerPath = Path.Combine(directory, "Caller.cs");
        var targetPath = Path.Combine(directory, "Targets.cs");
        var excludedPath = Path.Combine(directory, "Excluded.cs");
        var callerTree = CSharpSyntaxTree.ParseText("""
            partial class Demo
            {
                partial void Hook();
                void Run(Service service, Derived receiver, System.Action action)
                {
                    if (service is null) return;
                    service.Save(1);
                    service.Save("text");
                    Hook();
                    receiver.Virtual();
                    action();
                    Excluded.External();
                    Here();
                }
                void Here() {}
            }
            """, path: callerPath);
        var targetTree = CSharpSyntaxTree.ParseText("""
            partial class Demo
            {
                partial void Hook() {}
            }
            class Service
            {
                public void Save<T>(T value) where T : struct {}
                public void Save(string value) {}
            }
            class Derived
            {
                public virtual void Virtual() {}
            }
            """, path: targetPath);
        var excludedTree = CSharpSyntaxTree.ParseText(
            "static class Excluded { public static void External() {} }", path: excludedPath);
        var compilation = CSharpCompilation.Create("Fictional", [callerTree, targetTree, excludedTree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.IsEmpty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var files = new HashSet<string> { callerPath, targetPath };
        var caller = new SourceFile(compilation.GetSemanticModel(callerTree), navigableCSharpPaths: files);
        var target = new SourceFile(compilation.GetSemanticModel(targetTree), navigableCSharpPaths: files);
        var diagram = Diagram(caller);
        var saves = diagram.Nodes.Where(node => node.Label == "Service.Save()").ToArray();
        Assert.HasCount(2, saves);
        Assert.IsTrue(saves.All(node => node.TargetFilePath == targetPath));
        CollectionAssert.AreEquivalent(target.Methods.Where(method => method.Name == "Save")
            .Select(method => (int?)method.Id).ToArray(), saves.Select(node => node.TargetMethod).ToArray());
        var hook = Single(diagram, "Demo.Hook()");
        Assert.AreEqual(targetPath, hook.TargetFilePath);
        Assert.AreEqual(target.Methods.Single(method => method.Name == "Hook").Id, hook.TargetMethod);
        Assert.IsNotEmpty(target.Analyze(hook.TargetMethod!.Value).Nodes);
        Assert.IsNull(Single(diagram, "Derived.Virtual() · 実行時に決定").TargetMethod);
        Assert.DoesNotContain(node => node.Label.Contains("Invoke", StringComparison.Ordinal) &&
            node.TargetMethod is not null, diagram.Nodes);
        Assert.IsNull(Single(diagram, "Excluded.External()").TargetMethod);
        var here = Single(diagram, "Demo.Here()");
        Assert.IsNull(here.TargetFilePath);
        Assert.AreEqual(caller.Methods.Single(method => method.Name == "Here").Id, here.TargetMethod);
    }

    [TestMethod]
    public void SourceConditionsAndPreviewLabelFormatKeepTheirMeaning()
    {
        var result = Diagram(File("""
            class Demo
            {
                void Run() { if (Check() && Other(3)) Work(); }
                bool Check() => true;
                bool Other(int value) => value > 0;
                void Work() { }
            }
            """));
        var conditions = result.Nodes.Where(node => node.Kind == NodeKind.Condition).Select(node => node.Label).ToArray();
        CollectionAssert.AreEquivalent(new[] { "Check()", "Other(3)" }, conditions);
        Assert.DoesNotContain("  ·  L", MermaidWriter.Write(result, includeSourceLines: false));
        StringAssert.Contains(MermaidWriter.Write(result), "  ·  L");
    }

    [TestMethod]
    public void ShortCircuitAndTernaryFollowRoslynPolarity()
    {
        var result = Diagram(File("""
            class Demo
            {
                int Run() => A() && B() || C() ? Yes() : No();
                bool A() => true;
                bool B() => false;
                bool C() => true;
                int Yes() => 1;
                int No() => 2;
            }
            """));
        var a = Single(result, "Demo.A()");
        var b = Single(result, "Demo.B()");
        var c = Single(result, "Demo.C()");
        var yes = Single(result, "Demo.Yes()");
        var no = Single(result, "Demo.No()");
        Assert.AreEqual(b, Next(result, Next(result, a), "True"));
        Assert.AreEqual(c, Next(result, Next(result, a), "False"));
        Assert.AreEqual(yes, Next(result, Next(result, b), "True"));
        Assert.AreEqual(c, Next(result, Next(result, b), "False"));
        Assert.AreEqual(yes, Next(result, Next(result, c), "True"));
        Assert.AreEqual(no, Next(result, Next(result, c), "False"));
        Assert.AreEqual("return", Next(result, yes).Label);
        Assert.AreEqual("End", Next(result, Next(result, no)).Label);
    }

    [TestMethod]
    public void AssignmentsKeepReceiverIndexAndRhsCallsInOrder()
    {
        var result = Diagram(File("""
            class Demo
            {
                int P { get => 1; set { } }
                int this[int i] { get => i; set { } }
                Demo Receiver() => this;
                int Index() => 1;
                int Save() => 3;
                bool Check() => true;
                int Run()
                {
                    var hidden = 555;
                    hidden = Save();
                    Receiver()[Index()] = Check() ? Save() : hidden;
                    P += Save();
                    return P;
                }
            }
            """));
        var hiddenWrite = result.Nodes.Where(node => node.Label == "Demo.Save()").MinBy(node => node.Source!.Line)!;
        var receiver = Next(result, hiddenWrite);
        Assert.AreEqual("Demo.Receiver()", receiver.Label);
        var index = Next(result, receiver);
        Assert.AreEqual("Demo.Index()", index.Label);
        var check = Next(result, index);
        Assert.AreEqual("Demo.Check()", check.Label);
        var condition = Next(result, check);
        var saved = Next(result, condition, "True");
        Assert.AreEqual("Demo.Save()", saved.Label);
        var nextSave = Next(result, saved);
        Assert.AreEqual("Demo.Save()", nextSave.Label);
        Assert.AreEqual(nextSave, Next(result, condition, "False"));
        Assert.AreEqual("return", Next(result, nextSave).Label);
        Assert.DoesNotContain("555", MermaidWriter.Write(result));
    }

    [TestMethod]
    public void PropertyEvaluationSurvivesLoweredCompoundAndCoalescingAssignments()
    {
        var result = Diagram(File("""
            class Demo
            {
                int P { get => 1; set { } }
                string? S { get => null; set { } }
                private int slot;
                ref int R { get => ref slot; }
                bool Check() => true;
                int Save() => 3;
                string Make() => "ok";
                void Run()
                {
                    P += Check() ? Save() : 0;
                    S ??= Make();
                    R = Check() ? Save() : 0;
                }
            }
            """));
        var check = Next(result, Single(result, "Start"));
        Assert.AreEqual("Demo.Check()", check.Label);
        var firstCondition = Next(result, check);
        var nullCheck = Next(result, firstCondition, "False");
        Assert.AreEqual("null", nullCheck.Label);
        var make = Next(result, nullCheck, "True");
        Assert.AreEqual("Demo.Make()", make.Label);
        var nextCheck = Next(result, make);
        Assert.AreEqual("Demo.Check()", nextCheck.Label);
        Assert.AreEqual(nextCheck, Next(result, nullCheck, "False"));
    }

    [TestMethod]
    public void PropertyAndIndexerAccessorsDoNotAppearAsMethodsOrNodes()
    {
        var file = File("""
            class Demo
            {
                int Value { get => GetCount(); set { } }
                int Count => GetCount();
                int this[int i] => GetCount() + i;
                int Run() => Value + Count + this[1];
                int GetCount() => 2;
            }
            """);
        CollectionAssert.AreEqual(new[] { "Run", "GetCount" }, file.Methods.Select(method => method.Name).ToArray());
        Assert.IsEmpty(Diagram(file).Nodes.Where(node => node.Kind == NodeKind.Call));
    }

    [TestMethod]
    [DataRow("Math.Abs")]
    [DataRow("System.Math.Abs")]
    public void DottedExclusionNamesMatchLiteralSeparators(string name)
    {
        var file = File("""
            using System;
            class Demo
            {
                int Run() => Math.Abs(-1) + MathXAbs() + SystemXMath.Abs();
                int MathXAbs() => 2;
            }
            static class SystemXMath
            {
                public static int Abs() => 3;
            }
            """);
        var method = file.Methods.Single(entry => entry.Name == "Run");
        var result = file.Analyze(method.Id, excludedCalls: new CallExclusions([name]));

        Assert.DoesNotContain(node => node.Label == "Math.Abs()", result.Nodes);
        Assert.Contains(node => node.Label == "Demo.MathXAbs()", result.Nodes);
        Assert.Contains(node => node.Label == "SystemXMath.Abs()", result.Nodes);
    }

    [TestMethod]
    public void RegexExclusionsKeepArgumentEvaluationAndBranches()
    {
        var file = File("""
            using System;
            namespace Sample
            {
                class Demo
                {
                    int Run() => Math.Abs(Read()) > 0 ? Keep() : Quiet();
                    int Read() => -1;
                    int Keep() => 1;
                    int Quiet() => 0;
                }
            }
            """);
        var method = file.Methods.Single(entry => entry.Name == "Run");
        var result = file.Analyze(method.Id, excludedCalls: new CallExclusions(["Math.*", @"Sample\.Demo\.Quiet"]));
        var read = Single(result, "Demo.Read()");
        var condition = Next(result, read);
        Assert.AreEqual(NodeKind.Condition, condition.Kind);
        Assert.AreEqual("Demo.Keep()", Next(result, condition, "True").Label);
        Assert.AreEqual("return", Next(result, condition, "False").Label);
        Assert.DoesNotContain(node => node.Label is "Math.Abs()" or "Demo.Quiet()", result.Nodes);
    }

    [TestMethod]
    public void ExplicitKnownThrowRunsFinallyBeforeMatchingUnfilteredCatch()
    {
        var result = Diagram(File("""
            using System;
            class Demo
            {
                void Run()
                {
                    try
                    {
                        try { throw new ArgumentNullException(); }
                        finally { Cleanup(); }
                    }
                    catch (InvalidOperationException) { Other(); }
                    catch (ArgumentException) { Handle(); }
                }
                void Cleanup() {}
                void Other() {}
                void Handle() {}
            }
            """));
        CollectionAssert.AreEqual(new[] { "throw", "finally", "Demo.Cleanup()", "catch · System.ArgumentException", "Demo.Handle()", "End" },
            PathFrom(result, Single(result, "throw")).Select(node => node.Label).ToArray());
    }

    [TestMethod]
    public void AllLoopKindsExposeCorrectContinueBreakAndReturnDestinations()
    {
        var result = Diagram(File("""
            using System.Collections.Generic;
            class Demo
            {
                void Run(List<int> list)
                {
                    for (int i = 0; Check(); Update(ref i))
                    {
                        if (Skip()) continue;
                        if (Stop()) break;
                        Work();
                    }
                    foreach (var value in list)
                    {
                        if (Skip()) continue;
                        Work();
                    }
                    while (Check())
                    {
                        if (Skip()) continue;
                        if (Stop()) return;
                        Work();
                    }
                    do
                    {
                        if (Skip()) continue;
                        Work();
                    } while (Check());
                    Done();
                }
                bool Check() => true;
                bool Skip() => true;
                bool Stop() => true;
                void Update(ref int i) => i++;
                void Work() {}
                void Done() {}
            }
            """));
        var transfers = result.Nodes.Where(node => node.Label == "continue").OrderBy(node => node.Source!.Line).ToArray();
        Assert.AreEqual(4, transfers.Length);
        Assert.AreEqual("Demo.Update()", Next(result, transfers[0]).Label);
        Assert.AreEqual("Demo.Check()", Next(result, Next(result, transfers[0])).Label);
        StringAssert.Contains(Next(result, transfers[1]).Label, "MoveNext()");
        Assert.AreEqual("Demo.Check()", Next(result, transfers[2]).Label);
        Assert.AreEqual("Demo.Check()", Next(result, transfers[3]).Label);
        StringAssert.Contains(Next(result, Single(result, "break")).Label, "GetEnumerator()");
        Assert.AreEqual("End", Next(result, Single(result, "return")).Label);
    }

    [TestMethod]
    public void SwitchAndNestedLoopBreakKeepTheirOwnDestinations()
    {
        var result = Diagram(File("""
            class Demo
            {
                void Run(int n)
                {
                    while (Again())
                    {
                        switch (n)
                        {
                            case 1 when Check(): One(); break;
                            case 2: Two(); continue;
                            default: Other(); break;
                        }
                        Tail();
                    }
                    EndLoop();
                }
                bool Again() => true;
                bool Check() => true;
                void One() {}
                void Two() {}
                void Other() {}
                void Tail() {}
                void EndLoop() {}
            }
            """));
        Assert.AreEqual("Demo.Tail()", Next(result, Next(result, Single(result, "Demo.One()"))).Label);
        Assert.AreEqual("Demo.Tail()", Next(result, Next(result, Single(result, "Demo.Other()"))).Label);
        Assert.AreEqual("Demo.Again()", Next(result, Next(result, Single(result, "Demo.Two()"))).Label);
        Assert.AreEqual("Demo.EndLoop()", Next(result, Next(result, Single(result, "Demo.Again()")), "False").Label);
        var guard = Next(result, Single(result, "Demo.Check()"));
        Assert.AreEqual("Demo.One()", Next(result, guard, "True").Label);
        Assert.AreNotEqual("Demo.One()", Next(result, guard, "False").Label);
    }

    [TestMethod]
    public void SwitchInputAndCaseGrayOutPreserveOtherCasesAndSharedCode()
    {
        var result = Diagram(File("""
            class Demo
            {
                void Run(int status)
                {
                    switch (Read(status))
                    {
                        case 0: Zero(); break;
                        case 1:
                        case 2 when Allowed(): Shared(); break;
                        default: Fallback(); break;
                    }
                    Audit();
                }
                int Read(int value) => value;
                bool Allowed() => true;
                void Zero() {}
                void Shared() {}
                void Fallback() {}
                void Audit() {}
            }
            """));
        var input = Single(result, "switch (Read(status))");
        Assert.AreEqual(input, Next(result, Single(result, "Demo.Read()")));
        var first = Single(result, "case 0 に一致");
        var second = Single(result, "case 1 に一致");
        var guard = Single(result, "when Allowed()");
        Assert.IsTrue(first.IsCaseTest);
        Assert.IsTrue(guard.IsCaseTest);
        var annotations = new NodeAnnotations { GrayRoots = [first.Id] };
        var gray = annotations.GrayNodes(result);
        CollectionAssert.AreEquivalent(new[] { first.Id, Single(result, "Demo.Zero()").Id },
            result.Nodes.Where(node => (node.Kind is NodeKind.Call or NodeKind.Condition) &&
                gray.Contains(node.Id)).Select(node => node.Id).ToArray());
        annotations.GrayRoots.Clear();
        annotations.GrayRoots.Add(second.Id);
        Assert.IsFalse(annotations.GrayNodes(result).Contains(Single(result, "Demo.Shared()").Id));
    }

    [TestMethod]
    public void SwitchExpressionsDisplayInputAndArmsWithoutOperatorNodes()
    {
        var result = Diagram(File("""
            class Demo
            {
                decimal Read() => 2m;
                decimal Run(decimal input) => (input + Read()) switch
                {
                    3m => Read(),
                    _ => 0m
                };
            }
            """));
        var input = Single(result, "switch ((input + Read()))");
        var test = Single(result, "case 3m に一致");
        Assert.IsTrue(test.IsCaseTest);
        Assert.AreEqual(test, Next(result, input));
        Assert.HasCount(2, result.Nodes.Where(node => node.Label == "Demo.Read()"));
        Assert.DoesNotContain(node => node.Label.Contains("op_", StringComparison.Ordinal), result.Nodes);
    }

    [TestMethod]
    public void UsingCleanupIsHiddenButExplicitCleanupAndControlFlowRemain()
    {
        var result = Diagram(File("""
            using System;
            class Demo
            {
                void Run()
                {
                    using var resource = Open();
                    try
                    {
                        using (var second = Open())
                        {
                            if (Check()) return;
                            Work();
                        }
                        After();
                    }
                    finally { Explicit(); resource.Dispose(); }
                }
                IDisposable Open() => null!;
                bool Check() => true;
                void Work() { }
                void After() { }
                void Explicit() { }
            }
            """));
        Assert.IsFalse(result.Nodes.Any(node => node.Label == "null"));
        Assert.Contains(node => node.Label == "Demo.Open()", result.Nodes);
        Assert.Contains(node => node.Label == "Demo.Work()", result.Nodes);
        Assert.Contains(node => node.Label == "Demo.After()", result.Nodes);
        Assert.Contains(node => node.Label == "finally", result.Nodes);
        Assert.Contains(node => node.Label == "Demo.Explicit()", result.Nodes);
        Assert.Contains(node => node.Label == "IDisposable.Dispose() · 実行時に決定", result.Nodes);
        var returnNode = Single(result, "return");
        var returnPath = PathFrom(result, returnNode).Select(node => node.Label).ToArray();
        CollectionAssert.Contains(returnPath, "Demo.Explicit()");
        CollectionAssert.Contains(returnPath, "End");
    }

    [TestMethod]
    public void AwaitUsingCleanupIsHiddenAndExplicitAwaitRemains()
    {
        var result = Diagram(File("""
            using System;
            using System.Threading.Tasks;
            class Demo
            {
                async Task Run()
                {
                    await using (var resource = Open())
                    {
                        await Work();
                    }
                    await After();
                }
                IAsyncDisposable Open() => null!;
                Task Work() => Task.CompletedTask;
                Task After() => Task.CompletedTask;
            }
            """));
        Assert.AreEqual(2, result.Nodes.Count(node => node.Label == "await"));
        Assert.Contains(node => node.Label == "Demo.Open()", result.Nodes);
        Assert.Contains(node => node.Label == "Demo.Work()", result.Nodes);
        Assert.Contains(node => node.Label == "Demo.After()", result.Nodes);
        Assert.DoesNotContain(node => node.Label.Contains("DisposeAsync") || node.Label.Contains("finally")
            || node.Label == "null" || node.Label.Contains("巻き戻し"), result.Nodes);
    }

    [TestMethod]
    public void FinallyRunsBeforeDistinctReturnAndNormalContinuations()
    {
        var result = Diagram(File("""
            using System;
            class Demo
            {
                int Run(bool stop, bool fail)
                {
                    try
                    {
                        try
                        {
                            if (stop) return Value();
                            if (fail) throw new Exception();
                        }
                        finally { Inner(); }
                    }
                    finally { Outer(); }
                    Done();
                    return 0;
                }
                int Value() => 1;
                void Inner() {}
                void Outer() {}
                void Done() {}
            }
            """));
        CollectionAssert.AreEqual(new[] { "Demo.Value()", "return", "finally", "Demo.Inner()", "finally", "Demo.Outer()", "End" },
            PathFrom(result, Single(result, "Demo.Value()")).Select(node => node.Label).ToArray());
        CollectionAssert.AreEqual(new[] { "throw", "finally", "Demo.Inner()", "finally", "Demo.Outer()", "呼び出し元へ例外を伝播" },
            PathFrom(result, Single(result, "throw")).Select(node => node.Label).ToArray());
        Assert.Contains(node => PathFrom(result, node).Any(step => step.Label == "Demo.Done()"),
            result.Nodes.Where(node => node.Label == "Demo.Inner()"));
        Assert.Contains(message => message.Contains("例外探索"), result.Notices);
    }

    [TestMethod]
    public void FilterFalseDoesNotJumpToAnInventedHandlerAndRethrowMapsToSource()
    {
        var source = File("""
            using System;
            class Demo
            {
                void Run()
                {
                    try { Work(); }
                    catch (InvalidOperationException) when (Check()) { throw; }
                    catch (Exception) { Other(); }
                    finally { Cleanup(); }
                }
                void Work() {}
                bool Check() => true;
                void Other() {}
                void Cleanup() {}
            }
            """);
        var result = Diagram(source);
        var check = Next(result, Single(result, "Demo.Check()"));
        var rethrow = Next(result, check, "True");
        Assert.AreEqual("throw · 再スロー", rethrow.Label);
        Assert.AreEqual("throw", source.Text.Substring(rethrow.Source!.Offset, rethrow.Source.Length));
        StringAssert.Contains(Next(result, check, "False").Label, "フィルター不成立");
        Assert.Contains(node => node.Label == "Demo.Cleanup()", PathFrom(result, rethrow));
        Assert.Contains(node => node.Label.StartsWith("catch · System.Exception"), result.Nodes);
    }

    [TestMethod]
    public void SameFileSymbolsResolveByOverloadButDynamicAndExternalCallsDoNot()
    {
        var source = File("""
            class Demo
            {
                void Run(Demo other, dynamic item)
                {
                    Pick(1);
                    Pick("s");
                    other.Virtual();
                    item.Pick(2);
                    Other.External();
                    Local();
                    return;
                    void Local() => Run(this, item);
                }
                private void Pick(int value) => Run(this, 1);
                private void Pick(string value) => Pick(2);
                public virtual void Virtual() {}
            }
            """, "static class Other { public static void External() {} }");

        var diagram = Diagram(source);
        var calls = diagram.Nodes.Where(node => node.Label == "Demo.Pick()").OrderBy(node => node.Source!.Line).ToArray();
        Assert.AreEqual(2, calls.Length);
        Assert.AreEqual(2, calls.Select(node => node.TargetMethod).Distinct().Count());
        foreach (var call in calls)
            Assert.IsNotNull(call.TargetMethod);
        Assert.IsNull(Single(diagram, "Demo.Virtual() · 実行時に決定").TargetMethod);
        Assert.IsNull(Single(diagram, "Other.External()").TargetMethod);
        Assert.IsNull(Single(diagram, "呼び出し · 実行時に決定").TargetMethod);
        Assert.Contains(node => node.Label.EndsWith("Local()") && node.TargetMethod is not null, diagram.Nodes);
        var pick = source.Analyze(calls[0].TargetMethod!.Value);
        Assert.AreEqual(source.Methods.Single(method => method.Name == "Run").Id,
            pick.Nodes.Single(node => node.Label == "Demo.Run()").TargetMethod);
        Assert.IsNotEmpty(Diagram(source, "Local").Nodes);
    }

    [TestMethod]
    public void UnresolvedInvocationAndDeadBranchAreVisible()
    {
        var result = Diagram(File("""
            class Demo
            {
                void Run()
                {
                    if (false) Missing();
                    End();
                }
                void End() {}
            }
            """, allowErrors: true));
        Assert.IsTrue(result.IsPartial);
        Assert.IsFalse(Single(result, "呼び出し · 未解決").IsReachable);
        Assert.Contains(edge => edge.Label == "True" && !edge.IsReachable, result.Edges);
        Assert.IsTrue(Single(result, "Demo.End()").IsReachable);
    }

    [TestMethod]
    public void MermaidSnapshotRoundTripsThroughBrowserFixture()
    {
        var file = File(""""""
            class 例
            {
                bool Run(string 値) =>
                    値 == "a|#34;[\"<script> & ;" ? 保存() : 保存("改行\n引用");
                bool 保存() => true;
                bool 保存(string 値) => true;
            }
            """""");
        var result = Diagram(file);
        var fixture = MermaidWriter.Write(result);
        StringAssert.Contains(fixture, "保存");
        StringAssert.Contains(fixture, "#34;");
        StringAssert.Contains(fixture, "#124;");
        Assert.DoesNotContain("<script>", fixture);
        Assert.AreEqual(fixture, MermaidWriter.Write(Diagram(file)));
        if (Environment.GetEnvironmentVariable("CFV_MERMAID_FIXTURE") is { } export)
            System.IO.File.WriteAllText(export, fixture);
    }

    [TestMethod]
    public void TwentyThousandLinesWithManyMethodsAndOneLargeMethod()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var little = "class Many {\n" + string.Concat(Enumerable.Range(0, 4_000)
            .Select(i => $"int M{i}()\n{{\n return {i};\n}}\n\n")) + "}";
        var smallFile = File(little);
        var listed = stopwatch.Elapsed;
        var small = smallFile.Analyze(smallFile.Methods[^1].Id);
        var smallTime = stopwatch.Elapsed - listed;
        Assert.AreEqual(4_000, smallFile.Methods.Count);
        Assert.Contains(node => node.Label == "return", small.Nodes);
        stopwatch.Restart();
        var big = "class Huge { void Work() {}\nvoid Run(bool flag) {\n" +
            string.Concat(Enumerable.Repeat("if (flag)\n{\n Work();\n}\n\n", 4_000)) + "}}";
        var largeFile = File(big);
        var largeListed = stopwatch.Elapsed;
        var large = Diagram(largeFile);
        var largeTime = stopwatch.Elapsed - largeListed;
        var mermaid = MermaidWriter.Write(large);
        Assert.AreEqual(4_000, large.Nodes.Count(node => node.Kind == NodeKind.Call));
        Assert.AreEqual(4_000, large.Nodes.Count(node => node.Kind == NodeKind.Condition));
        testContext.WriteLine($"Many: list {listed.TotalMilliseconds:F0} ms, CFG {smallTime.TotalMilliseconds:F0} ms. " +
            $"Huge: list {largeListed.TotalMilliseconds:F0} ms, CFG {largeTime.TotalMilliseconds:F0} ms, " +
            $"{large.Nodes.Count} nodes / {large.Edges.Count} edges / {mermaid.Length} chars.");
    }
}
