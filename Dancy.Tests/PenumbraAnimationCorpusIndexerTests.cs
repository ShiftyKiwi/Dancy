using Dancy.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Dancy.Tests;

public class PenumbraAnimationCorpusIndexerTests
{
    [Fact]
    public void IndexesDirectoryOptionsAndDeterministicallySummarizesMultiSectionPap()
    {
        using var fixture = new CorpusFixture();
        fixture.Write("Fixture/meta.json", """
        {
          "FileVersion": 4,
          "Identifier": "fixture-id",
          "Name": "Fixture Mod",
          "Author": "Fixture Author",
          "Version": "1.0",
          "DefaultData": { "Files": {
            "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap": "default/idle.pap"
          } },
          "Groups": [{
            "Name": "Race selection",
            "Type": "Single",
            "Priority": 4,
            "DefaultSettings": 1,
            "Options": [
              { "Name": "Midlander", "Files": {
                "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap": "midlander/idle.pap"
              } },
              { "Name": "Miqo'te", "Files": {
                "chara/human/c0701/animation/a0001/bt_common/resident/idle.pap": "miqote/idle.pap"
              } }
            ],
            "Containers": [{ "Name": "Combined", "Files": {
              "chara/human/c0901/animation/a0001/bt_common/resident/idle.pap": "combined/idle.pap"
            } }]
          }]
        }
        """);
        fixture.Write("Fixture/default/idle.pap", CreatePap("default", 1));
        fixture.Write("Fixture/midlander/idle.pap", CreatePap("midlander", 2));
        fixture.Write("Fixture/miqote/idle.pap", CreatePap("first", 3, "second", 4));
        fixture.Write("Fixture/combined/idle.pap", CreatePap("combined", 5));

        var first = PenumbraAnimationCorpusIndexer.Create(fixture.Root);
        var second = PenumbraAnimationCorpusIndexer.Create(fixture.Root);
        var mod = Assert.Single(first.Mods);
        var miqote = Assert.Single(mod.PapMappings, mapping => mapping.Scope.OptionName == "Miqo'te");

        Assert.Equal("Fixture Author", mod.Author);
        Assert.Equal(4, mod.PapMappings.Count);
        Assert.True(Assert.Single(mod.PapMappings, mapping => mapping.Scope.OptionName == "Miqo'te").Scope.DefaultSelected);
        Assert.False(Assert.Single(mod.PapMappings, mapping => mapping.Scope.OptionName == "Midlander").Scope.DefaultSelected);
        Assert.Null(Assert.Single(mod.PapMappings, mapping => mapping.Scope.OptionName == "Combined").Scope.DefaultSelected);
        Assert.Equal(2, miqote.Modded?.Sections.Count);
        Assert.Equal(new[] { "first", "second" }, miqote.Modded?.Sections.Select(section => section.AnimationName));
        Assert.Equal(first.Mods[0].PapMappings.Select(mapping => mapping.Modded?.FileSha256), second.Mods[0].PapMappings.Select(mapping => mapping.Modded?.FileSha256));
    }

    [Fact]
    public void ComparesWholeHavokAndTimelineWithoutClaimingPerSectionHavokBoundaries()
    {
        using var fixture = new CorpusFixture();
        var vanilla = CreatePap("first", 1, "second", 2, havokSeed: 8, timelineSeed: 20);
        var modded = CreatePap("first", 1, "second", 2, havokSeed: 9, timelineSeed: 21);
        fixture.Write("Fixture/meta.json", """
        { "FileVersion": 4, "Name": "Fixture", "DefaultData": { "Files": {
          "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap": "idle.pap"
        } } }
        """);
        fixture.Write("Fixture/idle.pap", modded);
        Assert.True(PapCorpusSummary.TryCreate(vanilla, out var vanillaSummary, out var error), error);

        var index = PenumbraAnimationCorpusIndexer.Create(fixture.Root, new Dictionary<string, PapCorpusSummary>
        {
            ["chara/human/c0101/animation/a0001/bt_common/resident/idle.pap"] = vanillaSummary!,
        });
        var comparison = Assert.Single(Assert.Single(index.Mods).PapMappings).Comparison;

        Assert.NotNull(comparison);
        Assert.False(comparison!.HavokPayloadMatches);
        Assert.All(comparison.Sections, section => Assert.Equal("unknown-section-boundary-in-different-whole-payload", section.HavokSectionStatus));
        Assert.Contains(comparison.Sections, section => !section.TimelineMatches);
    }

    [Fact]
    public void ScopedPayloadHashingKeepsAllStructureButOnlyHashesIdleAndMultiSectionPayloads()
    {
        using var fixture = new CorpusFixture();
        fixture.Write("Fixture/meta.json", """
        { "FileVersion": 4, "Name": "Fixture", "DefaultData": { "Files": {
          "chara/human/c0101/animation/a0001/bt_common/emote/loop_emot08_loop.pap": "single.pap",
          "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap": "idle.pap",
          "chara/human/c0701/animation/a0001/bt_common/emote/loop_emot18_loop.pap": "multi.pap"
        } } }
        """);
        fixture.Write("Fixture/single.pap", CreatePap("single", 0));
        fixture.Write("Fixture/idle.pap", CreatePap("idle", 0));
        fixture.Write("Fixture/multi.pap", CreatePap("first", 0, "second", 1));

        var index = PenumbraAnimationCorpusIndexer.Create(
            fixture.Root,
            includePayloadHashesByGamePath: path => path.EndsWith("/resident/idle.pap", StringComparison.OrdinalIgnoreCase));
        var mappings = Assert.Single(index.Mods).PapMappings;

        var single = Assert.Single(mappings, mapping => mapping.ModPath == "single.pap").Modded;
        var idle = Assert.Single(mappings, mapping => mapping.ModPath == "idle.pap").Modded;
        var multi = Assert.Single(mappings, mapping => mapping.ModPath == "multi.pap").Modded;
        Assert.NotNull(single);
        Assert.NotNull(idle);
        Assert.NotNull(multi);
        Assert.False(single!.PayloadHashesCaptured);
        Assert.Single(single.Sections);
        Assert.True(idle!.PayloadHashesCaptured);
        Assert.True(multi!.PayloadHashesCaptured);
        Assert.Collection(multi.Sections, _ => { }, _ => { });
    }

    [Fact]
    public void ReadsPmpDirectlyAndDoesNotModifyTheArchive()
    {
        using var fixture = new CorpusFixture();
        var archivePath = Path.Combine(fixture.Root, "archive.pmp");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "meta.json", """
            { "FileVersion": 4, "Name": "Archive Fixture", "Author": "Archive Author", "DefaultData": { "Files": {
              "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap": "pap/idle.pap"
            } } }
            """);
            var entry = archive.CreateEntry("pap/idle.pap");
            using var stream = entry.Open();
            stream.Write(CreatePap("archive", 0));
        }

        var beforeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)));
        var beforeWrite = File.GetLastWriteTimeUtc(archivePath);
        var index = PenumbraAnimationCorpusIndexer.Create(fixture.Root);
        var afterHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)));
        var afterWrite = File.GetLastWriteTimeUtc(archivePath);

        var mod = Assert.Single(index.Mods);
        Assert.Equal("Archive", mod.Source.Kind);
        Assert.Equal("Archive Author", mod.Author);
        Assert.NotNull(Assert.Single(mod.PapMappings).Modded);
        Assert.Equal(beforeHash, afterHash);
        Assert.Equal(beforeWrite, afterWrite);
    }

    [Fact]
    public void RecordsCorruptMetadataAndContinues()
    {
        using var fixture = new CorpusFixture();
        fixture.Write("Broken/meta.json", "{ definitely not json");
        fixture.Write("Valid/meta.json", """{ "FileVersion": 4, "Name": "Valid" }""");

        var index = PenumbraAnimationCorpusIndexer.Create(fixture.Root);

        Assert.Contains(index.Issues, issue => issue.Kind == "metadata");
        Assert.Contains(index.Mods, mod => mod.Name == "Valid");
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static byte[] CreatePap(string firstName, int firstHavok, string? secondName = null, int secondHavok = 0, byte havokSeed = 7, byte timelineSeed = 11)
    {
        var animationCount = secondName is null ? 1 : 2;
        const int headerOffset = 26;
        const int headerSize = 40;
        var havokOffset = headerOffset + headerSize * animationCount;
        const int havokSize = 8;
        var timelineOffset = havokOffset + havokSize;
        var size = timelineOffset + animationCount * 8;
        var bytes = new byte[size];
        BitConverter.GetBytes(0x20706170).CopyTo(bytes, 0);
        BitConverter.GetBytes((short)animationCount).CopyTo(bytes, 8);
        BitConverter.GetBytes(headerOffset).CopyTo(bytes, 14);
        BitConverter.GetBytes(havokOffset).CopyTo(bytes, 18);
        BitConverter.GetBytes(timelineOffset).CopyTo(bytes, 22);
        WriteHeader(bytes, headerOffset, firstName, firstHavok, 15);
        if (secondName is not null)
            WriteHeader(bytes, headerOffset + headerSize, secondName, secondHavok, 0);
        for (var index = 0; index < havokSize; index++)
            bytes[havokOffset + index] = (byte)(havokSeed + index);
        for (var index = 0; index < animationCount; index++)
        {
            var offset = timelineOffset + index * 8;
            bytes[offset] = (byte)(timelineSeed + index);
            BitConverter.GetBytes(8).CopyTo(bytes, offset + 4);
        }
        return bytes;
    }

    private static void WriteHeader(byte[] bytes, int offset, string name, int havokIndex, int type)
    {
        Encoding.UTF8.GetBytes(name).CopyTo(bytes, offset);
        BitConverter.GetBytes((short)type).CopyTo(bytes, offset + 32);
        BitConverter.GetBytes((short)havokIndex).CopyTo(bytes, offset + 34);
    }

    private sealed class CorpusFixture : IDisposable
    {
        public CorpusFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "dancy-corpus-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relativePath, string content) => Write(relativePath, Encoding.UTF8.GetBytes(content));

        public void Write(string relativePath, byte[] content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
