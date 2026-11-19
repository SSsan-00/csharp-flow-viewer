using System.Diagnostics;
using Flow.Analysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class ProjectTests
{
    [TestMethod]
    public async Task ProjectEvaluationAppliesFrameworkConfigurationReferencesAndFileInclusion()
    {
        var root = Path.Combine(Path.GetTempPath(), "cfv-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var lib = Directory.CreateDirectory(Path.Combine(root, "Library")).FullName;
            var app = Directory.CreateDirectory(Path.Combine(root, "Application")).FullName;
            var shared = Directory.CreateDirectory(Path.Combine(root, "Shared")).FullName;
            await File.WriteAllTextAsync(Path.Combine(lib, "Library.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>netstandard2.1</TargetFramework></PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(lib, "Library.cs"), """
                namespace Outside { public static class Service { public static int Save() => 1; } }
                """);
            var path = Path.Combine(app, "Application.csproj");
            await File.WriteAllTextAsync(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net9.0;netstandard2.1</TargetFrameworks>
                    <Configurations>Debug;Release;Review</Configurations>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(Configuration)' == 'Review'">
                    <DefineConstants>$(DefineConstants);REVIEW</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Library/Library.csproj" />
                    <Compile Remove="Excluded.cs" />
                    <None Remove="Excluded.cshtml;ReviewOnly.cshtml" />
                    <Content Include="ContentPage.cshtml" />
                    <Content Include="ReviewOnly.cshtml" Condition="'$(Configuration)' == 'Review'" />
                    <Content Include="../Shared/Linked.cshtml" Link="Pages/Linked.cshtml" />
                    <Content Include="obj/Generated.cshtml" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(app, "Handler.cshtml.cs"), """
                class Handler
                {
                    int Run() => Outside.Service.Save();
                    #if NET9_0
                    void InNine() {}
                    #else
                    void InStandard() {}
                    #endif
                    #if REVIEW
                    void InReview() {}
                    #endif
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(app, "Excluded.cs"), "invalid and excluded");
            foreach (var name in new[] { "View.cshtml", "ContentPage.cshtml", "ReviewOnly.cshtml", "Excluded.cshtml" })
                await File.WriteAllTextAsync(Path.Combine(app, name), "<p>Fake example</p>");
            await File.WriteAllTextAsync(Path.Combine(shared, "Linked.cshtml"), "<p>Linked example</p>");
            Directory.CreateDirectory(Path.Combine(app, "obj"));
            await File.WriteAllTextAsync(Path.Combine(app, "obj", "Generated.cshtml"), "<p>Generated example</p>");
            await RestoreAsync(path, root);

            var settings = await ProjectLoader.InspectAsync(path, TestContextToken);
            Assert.Contains(option => option is { Configuration: "Review", TargetFramework: "net9.0" }, settings.Options);
            Assert.Contains(option => option is { Configuration: "Debug", TargetFramework: "netstandard2.1" }, settings.Options);
            using var current = await ProjectLoader.OpenAsync(path, new ProjectOptions("Review", "net9.0"), TestContextToken);
            // Razor files now participate in the existing list, while C# parsing still uses Roslyn documents.
            var file = await current.ReadAsync(Assert.ContainsSingle(current.Files.Where(item => !item.IsRazor)), TestContextToken);
            CollectionAssert.AreEquivalent(new[] { "View.cshtml", "ContentPage.cshtml", "ReviewOnly.cshtml", "Linked.cshtml" },
                current.Files.Where(item => item.IsRazor).Select(item => Path.GetFileName(item.Path)).ToArray());
            var linked = Assert.ContainsSingle(current.Files.Where(item => item.Path == Path.Combine(shared, "Linked.cshtml")));
            Assert.IsNull(linked.Id);
            Assert.IsTrue(linked.IsRazor);
            var razor = await current.ReadRazorAsync(linked, TestContextToken);
            StringAssert.Contains(razor.Text, "Linked example");
            Assert.IsNotEmpty(razor.Analyze(razor.Entries[0].Id).Nodes);
            await Assert.ThrowsExactlyAsync<ProjectLoadException>(() => current.ReadAsync(linked, TestContextToken));
            Assert.Contains(method => method.Name == "InNine", file.Methods);
            Assert.Contains(method => method.Name == "InReview", file.Methods);
            Assert.DoesNotContain(method => method.Name == "InStandard", file.Methods);
            Assert.DoesNotContain(message => message.Contains("解析は部分的"), file.Notices.Concat(current.Notices));
            Assert.IsNull(Assert.ContainsSingle(file.Analyze(file.Methods[0].Id).Nodes
                .Where(node => node.Label == "Service.Save()")).TargetMethod);

            using var standard = await ProjectLoader.OpenAsync(path, new ProjectOptions("Debug", "netstandard2.1"), TestContextToken);
            var standardFile = await standard.ReadAsync(Assert.ContainsSingle(standard.Files.Where(item => !item.IsRazor)), TestContextToken);
            Assert.DoesNotContain(item => Path.GetFileName(item.Path) == "ReviewOnly.cshtml", standard.Files);
            Assert.DoesNotContain(item => Path.GetFileName(item.Path) == "Excluded.cshtml", standard.Files);
            Assert.Contains(method => method.Name == "InStandard", standardFile.Methods);
            Assert.DoesNotContain(method => method.Name == "InReview", standardFile.Methods);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RazorPageResolvesNearestModelAndInheritedHandlersWithoutNonHandlers()
    {
        var root = Path.Combine(Path.GetTempPath(), "cfv-razor-project-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "RazorExample.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "_ViewImports.cshtml"), """
                @addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
                @using Example.Other
                """);
            var pagePath = Path.Combine(root, "Order.cshtml");
            await File.WriteAllTextAsync(pagePath, """
                @page
                @namespace Example.Pages
                @model OrderModel
                <form method="post">
                    <button asp-page-handler="Save">Save</button>
                    <button asp-page-handler="Override">Override</button>
                    <button asp-page-handler="Ignored">Ignored</button>
                    <button asp-page-handler="InheritedIgnored">InheritedIgnored</button>
                    <button asp-page-handler="Private">Private</button>
                    <button asp-page-handler="Static">Static</button>
                </form>
                """);
            var basePath = Path.Combine(root, "BaseHandlers.cs");
            await File.WriteAllTextAsync(basePath, """
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
                """);
            var modelPath = Path.Combine(root, "Order.cshtml.cs");
            await File.WriteAllTextAsync(modelPath, """
                namespace Example.Pages;
                public class OrderModel : BaseHandlers
                {
                    public override void OnPostOverride() { }
                    // NonHandler is inherited when a method is overridden, even without repeating the attribute.
                    public override void OnPostInheritedIgnored() { }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "OtherModels.cs"), """
                namespace Example { public class OrderModel : Microsoft.AspNetCore.Mvc.RazorPages.PageModel { } }
                namespace Example.Other { public class OrderModel : Microsoft.AspNetCore.Mvc.RazorPages.PageModel { } }
                """);
            await RestoreAsync(projectPath, root);
            using var project = await ProjectLoader.OpenAsync(projectPath, new ProjectOptions("Debug", "net9.0"), TestContextToken);
            var page = await project.ReadRazorAsync(project.Files.Single(item => item.Path == pagePath), TestContextToken);
            var model = await project.ReadAsync(project.Files.Single(item => item.Path == modelPath), TestContextToken);
            var baseModel = await project.ReadAsync(project.Files.Single(item => item.Path == basePath), TestContextToken);
            var links = page.Entries.SelectMany(entry => page.Analyze(entry.Id).Nodes)
                .Where(node => node.TargetFilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true)
                .DistinctBy(node => (node.TargetFilePath, node.TargetMethod)).ToArray();

            // The nearest namespace wins over the parent namespace and the imported namesake.
            Assert.DoesNotContain(message => message.Contains("候補が複数", StringComparison.Ordinal), page.Notices);
            var inherited = baseModel.Methods.Single(method => method.Name == "OnPostSave");
            var overridden = model.Methods.Single(method => method.Name == "OnPostOverride");
            CollectionAssert.AreEquivalent(new[] { (basePath, (int?)inherited.Id), (modelPath, (int?)overridden.Id) },
                links.Select(node => (node.TargetFilePath!, node.TargetMethod)).ToArray());
            Assert.IsTrue(links.All(node => node.Source?.FilePath == pagePath),
                "Handler links should keep their Razor trigger as the source and use the target path for navigation.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RestoreAsync(string projectPath, string emptyPackageSource)
    {
        // These fixtures use SDK reference packs only and must not depend on a public package feed.
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "restore", projectPath, "--source", emptyPackageSource, "-p:NuGetAudit=false" })
            start.ArgumentList.Add(argument);
        using var restore = Process.Start(start)!;
        var standardOutput = restore.StandardOutput.ReadToEndAsync();
        var standardError = restore.StandardError.ReadToEndAsync();
        await restore.WaitForExitAsync();
        Assert.IsTrue(restore.ExitCode == 0, await standardOutput + await standardError);
    }

    private static CancellationToken TestContextToken => CancellationToken.None;
}
