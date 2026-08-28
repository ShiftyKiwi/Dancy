using Dancy.Penumbra;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Dancy.Tests;

public class PenumbraMetadataScannerTests
{
    [Fact]
    public void ReadsDefaultOptionAndContainerFilesFromV4Metadata()
    {
        var meta = JObject.Parse("""
        {
          "FileVersion": 4,
          "Name": "Fixture Mod",
          "DefaultData": {
            "Files": {
              "chara/human/c0101/animation/a0001/bt_common/emote/default_loop.pap": "default/default_loop.pap"
            }
          },
          "Groups": [
            {
              "Name": "Pair Selection",
              "Options": [
                {
                  "Name": "Option A",
                  "Files": {
                    "chara/human/c0201/animation/a0001/bt_common/emote/option_loop.pap": "option/option_loop.pap"
                  }
                }
              ],
              "Containers": [
                {
                  "Name": "Container A",
                  "Files": {
                    "chara/human/c0301/animation/a0001/bt_common/emote/container_loop.pap": "container/container_loop.pap"
                  }
                }
              ]
            }
          ]
        }
        """);

        var mappings = PenumbraMetadataScanner.ScanV4(meta);

        Assert.Equal(3, mappings.Count);
        Assert.Contains(mappings, mapping => mapping.GroupName == "Fixture Mod" && mapping.OptionName == "(default)");
        Assert.Contains(mappings, mapping => mapping.GroupName == "Pair Selection" && mapping.OptionName == "Option A");
        Assert.Contains(mappings, mapping => mapping.GroupName == "Pair Selection" && mapping.OptionName == "Container A");
    }
}
