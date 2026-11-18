using Flow.Analysis;
using Flow.Mermaid;
using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace Flow.Tests;

public sealed class AnalysisTests(ITestOutputHelper output)
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
            Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return new SourceFile(compilation.GetSemanticModel(tree));
    }

    private static FlowDiagram Diagram(SourceFile file, string method = "Run") =>
        file.Analyze(Assert.Single(file.Methods, item => item.Name == method).Id);

    private static FlowNode Single(FlowDiagram diagram, string label) =>
        Assert.Single(diagram.Nodes, node => node.Label == label);

    private static FlowNode Next(FlowDiagram diagram, FlowNode from, string? branch = null) =>
        diagram.Nodes.Single(node => node.Id == Assert.Single(diagram.Edges, edge =>
            edge.From == from.Id && edge.Label == branch && edge.IsReachable).To);

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

    [Fact]
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
        Assert.Equal(b, Next(result, Next(result, a), "True"));
        Assert.Equal(c, Next(result, Next(result, a), "False"));
        Assert.Equal(yes, Next(result, Next(result, b), "True"));
        Assert.Equal(c, Next(result, Next(result, b), "False"));
        Assert.Equal(yes, Next(result, Next(result, c), "True"));
        Assert.Equal(no, Next(result, Next(result, c), "False"));
        Assert.Equal("return", Next(result, yes).Label);
        Assert.Equal("End", Next(result, Next(result, no)).Label);
    }

    [Fact]
    public void AssignmentsKeepReceiverIndexGetterRhsAndSetterInOrder()
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
        Assert.Equal("Demo.Receiver()", receiver.Label);
        var index = Next(result, receiver);
        Assert.Equal("Demo.Index()", index.Label);
        var check = Next(result, index);
        Assert.Equal("Demo.Check()", check.Label);
        var condition = Next(result, check);
        var saved = Next(result, condition, "True");
        Assert.Equal("Demo.Save()", saved.Label);
        var setter = Next(result, saved);
        Assert.EndsWith(" · set", setter.Label);
        Assert.NotNull(setter.TargetMethod);
        Assert.Equal(setter, Next(result, condition, "False"));
        Assert.Equal("Demo.P · get", Next(result, setter).Label);
        Assert.Equal("Demo.Save()", Next(result, Next(result, setter)).Label);
        Assert.Equal("Demo.P · set", Next(result, Next(result, Next(result, setter))).Label);
        Assert.DoesNotContain("555", MermaidWriter.Write(result));
    }

    [Fact]
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
        var getter = Single(result, "Demo.P · get");
        Assert.Equal("Demo.Check()", Next(result, getter).Label);
        var stringGetter = Single(result, "Demo.S · get");
        var nullCheck = Next(result, stringGetter);
        Assert.Equal("null？", nullCheck.Label);
        Assert.Equal("Demo.Make()", Next(result, nullCheck, "True").Label);
        Assert.Equal("Demo.S · set", Next(result, Single(result, "Demo.Make()")).Label);
        var referenceGetter = Single(result, "Demo.R · get");
        Assert.Equal("Demo.Check()", Next(result, referenceGetter).Label);
    }

    [Fact]
    public void ExpressionBodiedGetterHasItsOwnDiagram()
    {
        var file = File("""
            class Demo
            {
                int Count => GetCount();
                int this[int i] => GetCount() + i;
                int Run() => Count + this[1];
                int GetCount() => 2;
            }
            """);
        var result = Diagram(file);
        var getters = result.Nodes.Where(node => node.Label.EndsWith(" · get", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, getters.Length);
        foreach (var getter in getters)
        {
            Assert.NotNull(getter.TargetMethod);
            Assert.Contains(file.Analyze(getter.TargetMethod.Value).Nodes, node => node.Label == "Demo.GetCount()");
        }
    }

    [Fact]
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
        Assert.Equal(["throw", "finally", "Demo.Cleanup()", "catch · System.ArgumentException", "Demo.Handle()", "End"],
            PathFrom(result, Single(result, "throw")).Select(node => node.Label));
    }

    [Fact]
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
        Assert.Equal(4, transfers.Length);
        Assert.Equal("Demo.Update()", Next(result, transfers[0]).Label);
        Assert.Equal("Demo.Check()", Next(result, Next(result, transfers[0])).Label);
        Assert.Contains("MoveNext()", Next(result, transfers[1]).Label);
        Assert.Equal("Demo.Check()", Next(result, transfers[2]).Label);
        Assert.Equal("Demo.Check()", Next(result, transfers[3]).Label);
        Assert.Contains("GetEnumerator()", Next(result, Single(result, "break")).Label);
        Assert.Equal("End", Next(result, Single(result, "return")).Label);
    }

    [Fact]
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
        Assert.Equal("Demo.Tail()", Next(result, Next(result, Single(result, "Demo.One()"))).Label);
        Assert.Equal("Demo.Tail()", Next(result, Next(result, Single(result, "Demo.Other()"))).Label);
        Assert.Equal("Demo.Again()", Next(result, Next(result, Single(result, "Demo.Two()"))).Label);
        Assert.Equal("Demo.EndLoop()", Next(result, Next(result, Single(result, "Demo.Again()")), "False").Label);
        var guard = Next(result, Single(result, "Demo.Check()"));
        Assert.Equal("Demo.One()", Next(result, guard, "True").Label);
        Assert.NotEqual("Demo.One()", Next(result, guard, "False").Label);
    }

    [Fact]
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
        Assert.Equal(["Demo.Value()", "return", "finally", "Demo.Inner()", "finally", "Demo.Outer()", "End"],
            PathFrom(result, Single(result, "Demo.Value()")).Select(node => node.Label));
        Assert.Equal(["throw", "finally", "Demo.Inner()", "finally", "Demo.Outer()", "呼び出し元へ例外を伝播"],
            PathFrom(result, Single(result, "throw")).Select(node => node.Label));
        Assert.Contains(result.Nodes.Where(node => node.Label == "Demo.Inner()"), node =>
            PathFrom(result, node).Any(step => step.Label == "Demo.Done()"));
        Assert.Contains(result.Notices, message => message.Contains("例外探索"));
    }

    [Fact]
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
        Assert.Equal("throw · 再スロー", rethrow.Label);
        Assert.Equal("throw", source.Text.Substring(rethrow.Source!.Offset, rethrow.Source.Length));
        Assert.Contains("フィルター不成立", Next(result, check, "False").Label);
        Assert.Contains(PathFrom(result, rethrow), node => node.Label == "Demo.Cleanup()");
        Assert.Contains(result.Nodes, node => node.Label.StartsWith("catch · System.Exception"));
    }

    [Fact]
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
        Assert.Equal(2, calls.Length);
        Assert.Equal(2, calls.Select(node => node.TargetMethod).Distinct().Count());
        Assert.All(calls, node => Assert.NotNull(node.TargetMethod));
        Assert.Null(Single(diagram, "Demo.Virtual() · 実行時に決定").TargetMethod);
        Assert.Null(Single(diagram, "Other.External()").TargetMethod);
        Assert.Null(Single(diagram, "呼び出し · 実行時に決定").TargetMethod);
        Assert.Contains(diagram.Nodes, node => node.Label.EndsWith("Local()") && node.TargetMethod is not null);
        var pick = source.Analyze(calls[0].TargetMethod!.Value);
        Assert.Equal(source.Methods.Single(method => method.Name == "Run").Id,
            pick.Nodes.Single(node => node.Label == "Demo.Run()").TargetMethod);
        Assert.NotEmpty(Diagram(source, "Local").Nodes);
    }

    [Fact]
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
        Assert.True(result.IsPartial);
        Assert.False(Single(result, "呼び出し · 未解決").IsReachable);
        Assert.Contains(result.Edges, edge => edge.Label == "True" && !edge.IsReachable);
        Assert.True(Single(result, "Demo.End()").IsReachable);
    }

    [Fact]
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
        Assert.Contains("保存", fixture);
        Assert.Contains("#34;", fixture);
        Assert.Contains("#124;", fixture);
        Assert.DoesNotContain("<script>", fixture);
        Assert.Equal(fixture, MermaidWriter.Write(Diagram(file)));
        if (Environment.GetEnvironmentVariable("CFV_MERMAID_FIXTURE") is { } export)
            System.IO.File.WriteAllText(export, fixture);
    }

    [Fact]
    public void TwentyThousandLinesWithManyMethodsAndOneLargeMethod()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var little = "class Many {\n" + string.Concat(Enumerable.Range(0, 4_000)
            .Select(i => $"int M{i}()\n{{\n return {i};\n}}\n\n")) + "}";
        var smallFile = File(little);
        var listed = stopwatch.Elapsed;
        var small = smallFile.Analyze(smallFile.Methods[^1].Id);
        var smallTime = stopwatch.Elapsed - listed;
        Assert.Equal(4_000, smallFile.Methods.Count);
        Assert.Contains(small.Nodes, node => node.Label == "return");
        stopwatch.Restart();
        var big = "class Huge { void Work() {}\nvoid Run(bool flag) {\n" +
            string.Concat(Enumerable.Repeat("if (flag)\n{\n Work();\n}\n\n", 4_000)) + "}}";
        var largeFile = File(big);
        var largeListed = stopwatch.Elapsed;
        var large = Diagram(largeFile);
        var largeTime = stopwatch.Elapsed - largeListed;
        var mermaid = MermaidWriter.Write(large);
        Assert.Equal(4_000, large.Nodes.Count(node => node.Kind == NodeKind.Call));
        Assert.Equal(4_000, large.Nodes.Count(node => node.Kind == NodeKind.Condition));
        output.WriteLine($"Many: list {listed.TotalMilliseconds:F0} ms, CFG {smallTime.TotalMilliseconds:F0} ms. " +
            $"Huge: list {largeListed.TotalMilliseconds:F0} ms, CFG {largeTime.TotalMilliseconds:F0} ms, " +
            $"{large.Nodes.Count} nodes / {large.Edges.Count} edges / {mermaid.Length} chars.");
    }
}
