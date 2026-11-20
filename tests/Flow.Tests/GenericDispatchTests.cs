using Flow.Analysis;
using Flow.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class GenericDispatchTests
{
    [TestMethod]
    public void ConstructedGenericAndClosedImplementationsOfferOnlyMatchingOverloads()
    {
        var (caller, implementations) = Files("""
            class Caller
            {
                int Run()
                {
                    IStore<int> store = new Store<int>();
                    _ = new Store<long>();
                    _ = new Store<int>();
                    return store.Get();
                }
            }
            """, """
            interface IStore<T> { T Get(); T Get(int index); }
            sealed class IntStore : IStore<int>
            {
                public int Get() => 1;
                public int Get(int index) => 2;
            }
            sealed class Store<T> : IStore<T>
            {
                T IStore<T>.Get() => default!;
                T IStore<T>.Get(int index) => default!;
            }
            sealed class WrongStore : IStore<string>
            {
                public string Get() => "";
                public string Get(int index) => "";
            }
            """);

        var candidates = Candidates(caller, implementations, "Run");
        Assert.HasCount(2, candidates);
        Assert.AreEqual(implementations.Methods.Single(method => method.Signature == "IntStore.Get()").Id,
            candidates.Single(candidate => candidate.Label.StartsWith("IntStore.", StringComparison.Ordinal)).TargetMethod);
        Assert.AreEqual(implementations.Methods.Single(method => method.Signature == "Store<T>.IStore<T>.Get()").Id,
            candidates.Single(candidate => candidate.Label.StartsWith("Store<int>.", StringComparison.Ordinal)).TargetMethod);
    }

    [TestMethod]
    public void VariantInterfaceMembersResolveTheirActualSlotsWithoutIncludingInvalidConversions()
    {
        var (caller, implementations) = Files("""
            class Caller
            {
                object Read(IReader<object> reader) => reader.Get();
                void Write(IWriter<string> writer) => writer.Put("");
                object ReadInherited(IChild<object> reader) => reader.Get();
            }
            """, """
            interface IReader<out T> { T Get(); T Get(int index); }
            interface IChild<out T> : IReader<T> {}
            interface IWriter<in T> { void Put(T value); void Put(T value, int index); }
            sealed class StringReader : IChild<string>
            {
                public string Get() => "";
                public string Get(int index) => "";
            }
            sealed class IntReader : IReader<int>
            {
                public int Get() => 1;
                public int Get(int index) => 2;
            }
            sealed class ObjectWriter : IWriter<object>
            {
                void IWriter<object>.Put(object value) {}
                void IWriter<object>.Put(object value, int index) {}
            }
            sealed class ExactReader : IReader<string>, IReader<object>
            {
                string IReader<string>.Get() => "";
                string IReader<string>.Get(int index) => "";
                object IReader<object>.Get() => new();
                object IReader<object>.Get(int index) => new();
            }
            """);

        var readers = Candidates(caller, implementations, "Read");
        Assert.HasCount(2, readers);
        Assert.AreEqual(implementations.Methods.Single(method => method.Signature == "StringReader.Get()").Id,
            readers.Single(candidate => candidate.Label.StartsWith("StringReader.", StringComparison.Ordinal)).TargetMethod);
        Assert.AreEqual(implementations.Methods.Single(method => method.Signature == "ExactReader.IReader<object>.Get()").Id,
            readers.Single(candidate => candidate.Label.StartsWith("ExactReader.", StringComparison.Ordinal)).TargetMethod);

        Assert.AreEqual(implementations.Methods.Single(method => method.Signature == "ObjectWriter.IWriter<object>.Put(object value)").Id,
            Assert.ContainsSingle(Candidates(caller, implementations, "Write")).TargetMethod);
        Assert.AreEqual(implementations.Methods.Single(method => method.Signature == "StringReader.Get()").Id,
            Assert.ContainsSingle(Candidates(caller, implementations, "ReadInherited")).TargetMethod);
    }

    [TestMethod]
    public void VariantGenericCandidatesRequireClosedConstructionInTheSelectedMethod()
    {
        var (caller, implementations) = Files("""
            class Caller
            {
                object Read()
                {
                    Container<string>.Reader reader = new();
                    return ((IReader<object>)reader).Get();
                }
                object ReadOnlyParameter(IReader<object> reader) => reader.Get();
            }
            """, """
            interface IReader<out T> { T Get(); }
            class Container<T> where T : class
            {
                public sealed class Reader : IReader<T>
                {
                    public T Get() => default!;
                }
            }
            """);

        Assert.AreEqual(implementations.Methods.Single(method => method.Signature.EndsWith("Reader.Get()", StringComparison.Ordinal)).Id,
            Assert.ContainsSingle(Candidates(caller, implementations, "Read")).TargetMethod);
        Assert.IsEmpty(Candidates(caller, implementations, "ReadOnlyParameter"));
    }

    [TestMethod]
    public void InvalidGenericConstructionIsNotOffered()
    {
        var (caller, implementations) = Files("""
            class Caller
            {
                string Run(IStore<string> store)
                {
                    _ = new Store<string>();
                    return store.Get();
                }
            }
            """, """
            interface IStore<T> { T Get(); }
            sealed class Store<T> : IStore<T> where T : struct
            {
                public T Get() => default;
            }
            """, allowErrors: true);
        Assert.IsEmpty(Candidates(caller, implementations, "Run"));
    }

    private static IReadOnlyList<ImplementationCandidate> Candidates(SourceFile caller, SourceFile implementations, string method)
    {
        var graph = caller.Analyze(caller.Methods.Single(entry => entry.Name == method).Id);
        var call = Assert.ContainsSingle(graph.Nodes.Where(node => node.Kind == NodeKind.Call && node.RequiresRuntimeDispatch));
        Assert.IsNull(call.TargetMethod);
        Assert.IsNotNull(call.TargetDeclaration);
        Assert.IsNotNull(call.ImplementationCandidates);
        foreach (var candidate in call.ImplementationCandidates)
        {
            Assert.AreEqual(call.TargetDeclaration.FilePath, candidate.TargetFilePath);
            Assert.IsNotEmpty(implementations.Analyze(candidate.TargetMethod).Nodes);
        }
        return call.ImplementationCandidates;
    }

    private static (SourceFile Caller, SourceFile Implementations) Files(
        string callerText, string implementationsText, bool allowErrors = false)
    {
        var directory = Path.GetFullPath("FictionalProject");
        var caller = CSharpSyntaxTree.ParseText(callerText, path: Path.Combine(directory, "Caller.cs"));
        var implementations = CSharpSyntaxTree.ParseText(implementationsText, path: Path.Combine(directory, "Implementations.cs"));
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("Fictional", [caller, implementations], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (allowErrors) Assert.AreEqual("CS0453", Assert.ContainsSingle(errors).Id);
        else Assert.IsEmpty(errors);
        var paths = new HashSet<string> { caller.FilePath, implementations.FilePath };
        return (new SourceFile(compilation.GetSemanticModel(caller), navigableCSharpPaths: paths),
            new SourceFile(compilation.GetSemanticModel(implementations), navigableCSharpPaths: paths));
    }
}
