using Dancy.Domain;
using Dancy.Penumbra;
using Dancy.Persistence;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Dancy.Tests;

public class DancyMetadataMutatorTests
{
    [Fact]
    public void UpsertV4ReusesTheStableOptionInsteadOfAppendingAnotherOne()
    {
        var meta = JObject.Parse("""
        {
          "FileVersion": 4,
          "Groups": []
        }
        """);
        var plan = CreatePlan();

        DancyMetadataMutator.UpsertV4(meta, plan, new Dictionary<string, string>
        {
            ["chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap"] = "yucksdancy/paps/first.pap",
        });
        DancyMetadataMutator.UpsertV4(meta, plan, new Dictionary<string, string>
        {
            ["chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap"] = "yucksdancy/paps/second.pap",
        });

        var group = Assert.Single(meta["Groups"]!.Children<JObject>());
        var option = Assert.Single(group["Options"]!.Children<JObject>());
        Assert.Equal(plan.OverrideId, option["Id"]!.Value<string>());
        Assert.Equal("yucksdancy/paps/second.pap", option["Files"]!["chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap"]!.Value<string>());
    }

    [Fact]
    public void AtomicWriterLeavesThePreviousJsonAsABackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DancyTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "meta.json");
        try
        {
            AtomicJsonFile.Write(path, new JObject { ["FileVersion"] = 4, ["Name"] = "first" });
            AtomicJsonFile.Write(path, new JObject { ["FileVersion"] = 4, ["Name"] = "second" });

            Assert.Equal("second", JObject.Parse(File.ReadAllText(path))["Name"]!.Value<string>());
            Assert.Equal("first", JObject.Parse(File.ReadAllText(path + ".dancy.bak"))["Name"]!.Value<string>());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RealWriterRollsBackWhenReplacementFailsAtTheCheckpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DancyTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "meta.json");
        try
        {
            AtomicJsonFile.Write(path, new JObject { ["FileVersion"] = 4, ["Name"] = "original" });

            Assert.Throws<IOException>(() => AtomicJsonFile.Write(path, new JObject { ["FileVersion"] = 4, ["Name"] = "partial" }, new AtomicJsonWriteOptions
            {
                Checkpoint = checkpoint =>
                {
                    if (checkpoint == AtomicJsonWriteCheckpoint.BeforeReplacement)
                        throw new IOException("Injected replacement failure.");
                },
            }));

            Assert.Equal("original", JObject.Parse(File.ReadAllText(path))["Name"]!.Value<string>());
            Assert.Empty(Directory.GetFiles(directory, ".meta.json.*.tmp"));

            AtomicJsonFile.Write(path, new JObject { ["FileVersion"] = 4, ["Name"] = "recovered" });
            Assert.Equal("recovered", JObject.Parse(File.ReadAllText(path))["Name"]!.Value<string>());
            Assert.Equal("original", JObject.Parse(File.ReadAllText(path + ".dancy.bak"))["Name"]!.Value<string>());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RealGroupWriterIsIdempotentAndLeavesNoPartialOptionOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DancyTests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var metaPath = Path.Combine(directory, "meta.json");
            File.WriteAllText(metaPath, "{\"FileVersion\":4,\"Name\":\"Fixture\",\"Groups\":[]}");
            var plan = CreatePlan();
            var firstMappings = new Dictionary<string, string>
            {
                ["chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap"] = "yucksdancy/paps/first.pap",
            };

            PenumbraGroupWriter.CreateOrUpdateDancyGroup(directory, plan, firstMappings);
            PenumbraGroupWriter.CreateOrUpdateDancyGroup(directory, plan, firstMappings);
            var written = JObject.Parse(File.ReadAllText(metaPath));
            var dancyGroup = Assert.Single(written["Groups"]!.Children<JObject>(), DancyMetadataMutator.IsDancyGroup);
            Assert.Single(dancyGroup["Options"]!.Children<JObject>());

            Assert.Throws<IOException>(() => PenumbraGroupWriter.CreateOrUpdateDancyGroup(directory, plan, new Dictionary<string, string>
            {
                ["chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap"] = "yucksdancy/paps/replaced.pap",
            }, new AtomicJsonWriteOptions
            {
                Checkpoint = checkpoint =>
                {
                    if (checkpoint == AtomicJsonWriteCheckpoint.BeforeReplacement)
                        throw new IOException("Injected replacement failure.");
                },
            }));

            var afterFailure = JObject.Parse(File.ReadAllText(metaPath));
            var option = Assert.Single(Assert.Single(afterFailure["Groups"]!.Children<JObject>(), DancyMetadataMutator.IsDancyGroup)["Options"]!.Children<JObject>());
            Assert.Equal("yucksdancy/paps/first.pap", option["Files"]!["chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap"]!.Value<string>());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static OverridePlan CreatePlan()
        => OverridePlanner.Create(new OverridePlanRequest
        {
            ModIdentity = "test-mod",
            SourceGroupName = "Group",
            SourceOptionName = "Option",
            TargetTimelineKey = "emote/target_loop",
            TargetName = "Target",
            TargetCommand = "/target",
            Sources = new[]
            {
                new OverridePlanSource("chara/human/c0101/animation/a0001/bt_common/emote/source_loop.pap", "source.pap"),
            },
            TargetGamePaths = new[]
            {
                "chara/human/c0101/animation/a0001/bt_common/emote/target_loop.pap",
            },
        });
}
