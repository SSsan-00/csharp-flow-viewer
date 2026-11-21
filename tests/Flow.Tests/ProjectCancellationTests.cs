using Flow.Analysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class ProjectCancellationTests
{
    [TestMethod]
    public async Task CancelledInspectionAndOpenDoNotStartMSBuild()
    {
        var missingProject = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Unused.csproj");
        var cancelled = new CancellationToken(true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ProjectLoader.InspectAsync(missingProject, cancelled));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ProjectLoader.OpenAsync(missingProject, new("Debug", "net9.0"), cancelled));
    }
}
