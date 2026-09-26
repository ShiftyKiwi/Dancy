using Dancy.Pap;
using Xunit;

namespace Dancy.Tests;

public class PapFileInspectorTests
{
    [Fact]
    public void ReadsAStructurallyValidFixture()
    {
        var inspection = PapFileInspector.Inspect(CreateValidPap("fixture_event"));

        Assert.Equal(1, inspection.AnimationCount);
        Assert.Equal("fixture_event", Assert.Single(inspection.AnimationNames));
        Assert.Equal(7, Assert.Single(inspection.HavokIndices));
        Assert.Equal(8, Assert.Single(inspection.TimelineSectionSizes));
    }

    [Theory]
    [MemberData(nameof(MalformedFixtures))]
    public void RejectsMalformedPapFixtures(byte[] bytes)
    {
        Assert.ThrowsAny<InvalidDataException>(() => PapFileInspector.Inspect(bytes));
    }

    public static IEnumerable<object[]> MalformedFixtures()
    {
        yield return new object[] { Array.Empty<byte>() };
        yield return new object[] { CreateValidPap("fixture_event")[..20] };

        var invalidOffsets = CreateValidPap("fixture_event");
        BitConverter.GetBytes(9999).CopyTo(invalidOffsets, 22);
        yield return new object[] { invalidOffsets };

        var missingTimeline = CreateValidPap("fixture_event");
        BitConverter.GetBytes(0).CopyTo(missingTimeline, 74);
        yield return new object[] { missingTimeline };
    }

    internal static byte[] CreateValidPap(string animationName)
    {
        var bytes = new byte[78];
        BitConverter.GetBytes(0x20706170).CopyTo(bytes, 0);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 8);
        BitConverter.GetBytes(26).CopyTo(bytes, 14);
        BitConverter.GetBytes(66).CopyTo(bytes, 18);
        BitConverter.GetBytes(70).CopyTo(bytes, 22);
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(animationName);
        nameBytes.CopyTo(bytes, 26);
        BitConverter.GetBytes((short)7).CopyTo(bytes, 60);
        BitConverter.GetBytes(8).CopyTo(bytes, 74);
        return bytes;
    }
}
