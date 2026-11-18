using System.Text.Json;
using Flow.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flow.Tests;

[TestClass]
public sealed class FileViewStateTests
{
    [TestMethod]
    public void FileStateRoundTripsAndSeparatesProjectFileAndConfiguration()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(directory.FullName, FileViewState.Key("App", "A.cs", "Debug", "net9.0") + ".json");
            Assert.IsNull(FileViewState.Read(file));
            var state = new FileViewState(1, FileViewState.Key("Process()"), 17, 23, 11, 320.5,
                "diagram-key", "b3_2", new PreviewViewport(12, -300, 1.18),
                new Dictionary<string, NodeAnnotations> { ["diagram-key"] = new() { Marked = ["b3_2"], GrayRoots = ["b5_1"] } });
            state.Write(file);
            var restored = FileViewState.Read(file);
            Assert.IsNotNull(restored);
            Assert.AreEqual(JsonSerializer.Serialize(state), JsonSerializer.Serialize(restored));
            var updated = state with { Node = "b5_1" };
            updated.Write(file);
            Assert.AreEqual("b5_1", FileViewState.Read(file)?.Node);
            Assert.HasCount(1, directory.GetFiles());
            Assert.AreNotEqual(FileViewState.Key("App", "A.cs", "Debug", "net9.0"), FileViewState.Key("App", "B.cs", "Debug", "net9.0"));
            Assert.AreNotEqual(FileViewState.Key("App", "A.cs", "Debug", "net9.0"), FileViewState.Key("App", "A.cs", "Release", "net9.0"));
            Assert.AreNotEqual(FileViewState.Key("App", "A.cs", "Debug", "net9.0"), FileViewState.Key("App", "A.cs", "Debug", "net8.0"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
