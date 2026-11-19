using Flow.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

/// <summary>Exercises handler resolution in process, separately from the MSBuild integration tests.</summary>
[TestClass]
public sealed class RazorProjectBindingTests
{
    [TestMethod]
    [DataRow("Example.Pages", "OrderModel")]
    [DataRow("Example", "Pages.OrderModel")]
    [DataRow("Unrelated", "global::Example.Pages.OrderModel")]
    public async Task ResolvesModelScopeAndInheritedHandlers(string pageNamespace, string modelName)
    {
        var root = Path.Combine(Path.GetTempPath(), "cfv-binding-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var pagePath = Path.Combine(root, "Order.cshtml");
            var pageText = $$"""
                @page
                @namespace {{pageNamespace}}
                @model {{modelName}}
                <form method="post">
                    <button asp-page-handler="Save">Save</button>
                    <button asp-page-handler="Override">Override</button>
                    <button asp-page-handler="Ignored">Ignored</button>
                    <button asp-page-handler="InheritedIgnored">InheritedIgnored</button>
                    <button asp-page-handler="Private">Private</button>
                    <button asp-page-handler="Static">Static</button>
                </form>
                """;
            await File.WriteAllTextAsync(pagePath, pageText);
            await File.WriteAllTextAsync(Path.Combine(root, "_ViewImports.cshtml"), """
                @addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
                @using Example.Other
                """);
            var sources = new Dictionary<string, string>
            {
                ["BaseHandlers.cs"] = """
                    using Microsoft.AspNetCore.Mvc.RazorPages;
                    namespace Example.Pages;
                    public class BaseHandlers : PageModel
                    {
                        public void OnPostSave() { }
                        public virtual void OnPostOverride() { }
                        [NonHandler] public void OnPostIgnored() { }
                        [NonHandler] public virtual void OnPostInheritedIgnored() { }
                        private void OnPostPrivate() { }
                        public static void OnPostStatic() { }
                    }
                    """,
                ["Order.cshtml.cs"] = """
                    namespace Example.Pages;
                    public class OrderModel : BaseHandlers
                    {
                        public override void OnPostOverride() { }
                        public override void OnPostInheritedIgnored() { }
                    }
                    """,
                ["OtherModels.cs"] = """
                    namespace Example { public class OrderModel : Microsoft.AspNetCore.Mvc.RazorPages.PageModel { } }
                    namespace Example.Other { public class OrderModel : Microsoft.AspNetCore.Mvc.RazorPages.PageModel { } }
                    """
            };
            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
                "Example", "Example", LanguageNames.CSharp, filePath: Path.Combine(root, "Example.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: FrameworkReferences()));
            foreach (var (name, code) in sources)
            {
                var filePath = Path.Combine(root, name);
                await File.WriteAllTextAsync(filePath, code);
                project = project.AddDocument(name, SourceText.From(code), filePath: filePath).Project;
            }
            var compilation = await project.GetCompilationAsync();
            Assert.IsNotNull(compilation);
            Assert.IsEmpty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            // Construct the session from an already evaluated Roslyn project. This does not use a
            // BuildHost, and does not replace the separate end-to-end ProjectLoader tests.
            using var session = new ProjectSession(MSBuildWorkspace.Create(), project, [], null,
                new ProjectOptions("Debug", "net9.0"), [pagePath]);
            var page = await session.ReadRazorAsync(session.Files.Single(file => file.IsRazor), CancellationToken.None);
            var modelPath = Path.Combine(root, "Order.cshtml.cs");
            var basePath = Path.Combine(root, "BaseHandlers.cs");
            var model = await session.ReadAsync(session.Files.Single(file => file.Path == modelPath), CancellationToken.None);
            var baseModel = await session.ReadAsync(session.Files.Single(file => file.Path == basePath), CancellationToken.None);
            var links = page.Entries.SelectMany(entry => page.Analyze(entry.Id).Nodes)
                .Where(node => node.TargetFilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true)
                .DistinctBy(node => (node.TargetFilePath, node.TargetMethod)).ToArray();
            var inherited = baseModel.Methods.Single(method => method.Name == "OnPostSave");
            var overridden = model.Methods.Single(method => method.Name == "OnPostOverride");

            Assert.DoesNotContain(message => message.StartsWith("解析は部分的", StringComparison.Ordinal), page.Notices);
            CollectionAssert.AreEquivalent(new[] { (basePath, (int?)inherited.Id), (modelPath, (int?)overridden.Id) },
                links.Select(node => (node.TargetFilePath!, node.TargetMethod)).ToArray());
            Assert.IsTrue(links.All(node => node.Source?.FilePath == pagePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExternalLinkedViewReportsMissingLogicalImportContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "cfv-linked-binding-" + Guid.NewGuid());
        var projectDirectory = Directory.CreateDirectory(Path.Combine(root, "Project")).FullName;
        var sharedDirectory = Directory.CreateDirectory(Path.Combine(root, "Shared")).FullName;
        try
        {
            var linkedPath = Path.Combine(sharedDirectory, "View.cshtml");
            await File.WriteAllTextAsync(linkedPath, "<p>Linked view</p>");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "_ViewImports.cshtml"), "@namespace NotAutomaticallyInherited");
            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
                "Example", "Example", LanguageNames.CSharp, filePath: Path.Combine(projectDirectory, "Example.csproj")));
            using var session = new ProjectSession(MSBuildWorkspace.Create(), project, [], null,
                new ProjectOptions("Debug", "net9.0"), [linkedPath]);
            var page = await session.ReadRazorAsync(Assert.ContainsSingle(session.Files), CancellationToken.None);

            Assert.Contains(message => message.StartsWith("解析は部分的", StringComparison.Ordinal)
                && message.Contains("_ViewImports", StringComparison.Ordinal), page.Notices);
            Assert.IsNotEmpty(page.Analyze(-1).Nodes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<MetadataReference> FrameworkReferences()
    {
        // Use actual framework metadata, including PageModel and NonHandlerAttribute. The SDK used
        // by the application supplies the corresponding ASP.NET shared runtime alongside .NET.
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var sharedDirectory = Directory.GetParent(runtimeDirectory)!.Parent!.FullName;
        var aspNetDirectory = Path.Combine(sharedDirectory, "Microsoft.AspNetCore.App");
        var runtime = Directory.EnumerateDirectories(aspNetDirectory)
            .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
            .Where(item => item.Version is not null && item.Version.Major == Environment.Version.Major
                && item.Version.Minor == Environment.Version.Minor)
            .OrderByDescending(item => item.Version).First().Path;
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(Directory.EnumerateFiles(runtime, "*.dll"))
            .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
    }
}
