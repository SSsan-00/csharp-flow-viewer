using System.Diagnostics;
using Flow.Analysis;
using Xunit;

namespace Flow.Tests;

public sealed class ProjectTests
{
    [Fact]
    public async Task ProjectEvaluationAppliesFrameworkConfigurationReferencesAndFileInclusion()
    {
        var root = Path.Combine(Path.GetTempPath(), "cfv-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var lib = Directory.CreateDirectory(Path.Combine(root, "Library")).FullName;
            var app = Directory.CreateDirectory(Path.Combine(root, "Application")).FullName;
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
            await File.WriteAllTextAsync(Path.Combine(app, "Ignored.cshtml"), "<p>Fake example</p>");
            var restoreStart = new ProcessStartInfo("dotnet") { WorkingDirectory = app, RedirectStandardOutput = true };
            foreach (var argument in new[] { "restore", path, "--source", root, "-p:NuGetAudit=false" })
                restoreStart.ArgumentList.Add(argument);
            using var restore = Process.Start(restoreStart)!;
            var restoreLog = await restore.StandardOutput.ReadToEndAsync();
            await restore.WaitForExitAsync();
            Assert.True(restore.ExitCode == 0, restoreLog);

            var settings = await ProjectLoader.InspectAsync(path, TestContextToken);
            Assert.Contains(settings.Options, option => option is { Configuration: "Review", TargetFramework: "net9.0" });
            Assert.Contains(settings.Options, option => option is { Configuration: "Debug", TargetFramework: "netstandard2.1" });
            using var current = await ProjectLoader.OpenAsync(path, new ProjectOptions("Review", "net9.0"), TestContextToken);
            var file = await current.ReadAsync(Assert.Single(current.Files), TestContextToken);
            Assert.Contains(file.Methods, method => method.Name == "InNine");
            Assert.Contains(file.Methods, method => method.Name == "InReview");
            Assert.DoesNotContain(file.Methods, method => method.Name == "InStandard");
            Assert.DoesNotContain(file.Notices.Concat(current.Notices), message => message.Contains("解析は部分的"));
            Assert.Null(Assert.Single(file.Analyze(file.Methods[0].Id).Nodes,
                node => node.Label == "Service.Save()").TargetMethod);

            using var standard = await ProjectLoader.OpenAsync(path, new ProjectOptions("Debug", "netstandard2.1"), TestContextToken);
            var standardFile = await standard.ReadAsync(Assert.Single(standard.Files), TestContextToken);
            Assert.Contains(standardFile.Methods, method => method.Name == "InStandard");
            Assert.DoesNotContain(standardFile.Methods, method => method.Name == "InReview");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CancellationToken TestContextToken => CancellationToken.None;
}
