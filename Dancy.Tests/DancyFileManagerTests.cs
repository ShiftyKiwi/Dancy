using Dancy.Files;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Dancy.Tests;

public class DancyFileManagerTests
{
    [Fact]
    public void RemovesOnlyTheRequestedOverrideAndCollectsOnlyItsUnreferencedFiles()
    {
        using var fixture = new DancyModFixture();
        fixture.WriteMeta("""
        {
          "FileVersion": 4,
          "Groups": [
            { "Name": "Yuck's Dancy", "Options": [
              { "Id": "first", "Name": "First", "Files": { "chara/human/c0101/a.pap": "yucksdancy/paps/first.pap", "chara/human/c0201/a.pap": "yucksdancy/paps/shared.pap" } },
              { "Id": "second", "Name": "Second", "Files": { "chara/human/c0501/a.pap": "yucksdancy/paps/shared.pap", "chara/human/c0601/a.pap": "yucksdancy/paps/second.pap" } }
            ] },
            { "Name": "Source", "Options": [] }
          ]
        }
        """);
        fixture.WriteGenerated("first.pap");
        fixture.WriteGenerated("shared.pap");
        fixture.WriteGenerated("second.pap");
        fixture.WriteGenerated("ec3d96f92d8748d49215a7057eab2cdf.pap");
        fixture.WriteSource("files/source.pap");

        var result = DancyFileManager.RemoveDancyOverride(fixture.Root, "first");

        Assert.True(result.Changed);
        Assert.Equal(1, result.RemovedOverrideCount);
        Assert.False(result.DiskClean);
        Assert.True(result.RequestedOverrideCleanupSucceeded);
        Assert.Contains("DISK CLEAN FOR SELECTED OVERRIDE", result.Verification, StringComparison.Ordinal);
        Assert.Single(DancyFileManager.GetDancyOverrides(fixture.Root));
        Assert.DoesNotContain("yucksdancy/paps/shared.pap", result.GarbageCollection.RemovedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("yucksdancy/paps/first.pap", result.GarbageCollection.RemovedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("yucksdancy/paps/ec3d96f92d8748d49215a7057eab2cdf.pap", result.GarbageCollection.RemovedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "files", "source.pap")));
    }

    [Fact]
    public void RemovingTheLastOverrideRemovesTheEmptyGroupAndAllDancyFiles()
    {
        using var fixture = new DancyModFixture();
        fixture.WriteMeta("""
        { "FileVersion": 4, "Groups": [ { "Name": "Yuck's Dancy", "Options": [
          { "Id": "only", "Name": "Only", "Files": { "chara/human/c0101/a.pap": "yucksdancy/paps/only.pap" } }
        ] } ] }
        """);
        fixture.WriteGenerated("only.pap");
        fixture.WriteGenerated("legacy-guid.pap");

        var result = DancyFileManager.RemoveDancyOverride(fixture.Root, "only");

        Assert.True(result.DiskClean);
        Assert.True(result.RequestedOverrideCleanupSucceeded);
        Assert.Empty(DancyFileManager.GetDancyOverrides(fixture.Root));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "yucksdancy")));
        var meta = JObject.Parse(File.ReadAllText(Path.Combine(fixture.Root, "meta.json")));
        Assert.DoesNotContain(meta["Groups"]!.Children<JObject>(), group => group["Name"]?.ToString() == "Yuck's Dancy");
    }

    [Fact]
    public void RemoveAllHandlesLegacyGroupAndGuidNamedOutput()
    {
        using var fixture = new DancyModFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "group_001_yucksdancy.json"), """
        { "Name": "Yuck's Dancy", "Options": [
          { "DancyOverrideId": "legacy", "Name": "Legacy", "Files": { "chara/human/c0101/a.pap": "yucksdancy/paps/8f3b95458d7f4de6a0ee1fe019c78348.pap" } }
        ] }
        """);
        fixture.WriteGenerated("8f3b95458d7f4de6a0ee1fe019c78348.pap");
        fixture.WriteGenerated("unreferenced.pap");

        var result = DancyFileManager.RemoveAllDancyOverrides(fixture.Root);

        Assert.True(result.DiskClean);
        Assert.True(result.Changed);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "group_001_yucksdancy.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "yucksdancy")));
    }

    [Fact]
    public void RemoveAllClearsAnEmptyLegacyDancyGroup()
    {
        using var fixture = new DancyModFixture();
        fixture.WriteMeta("""
        { "FileVersion": 4, "Groups": [ { "Name": "Yuck's Dancy", "Options": [] } ] }
        """);

        var result = DancyFileManager.RemoveAllDancyOverrides(fixture.Root);

        Assert.True(result.DiskClean);
        Assert.False(DancyFileManager.DancyExists(fixture.Root));
    }

    private sealed class DancyModFixture : IDisposable
    {
        public DancyModFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"DancyFileManagerTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void WriteMeta(string json)
            => File.WriteAllText(Path.Combine(Root, "meta.json"), json);

        public void WriteGenerated(string name)
        {
            var path = Path.Combine(Root, "yucksdancy", "paps", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name);
        }

        public void WriteSource(string relativePath)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "source");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
