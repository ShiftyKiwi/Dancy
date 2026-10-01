using Dancy.Domain;

namespace Dancy.Pap;

// The service tests exercise invalid-plan cancellation only; this keeps the
// linked service independent of Dalamud/VFXEditor runtime dependencies.
public static class SourceAnimationSelectionResolver
{
    public static SourceAnimationSelectionResult Resolve(string _, PlannedPapCopy __)
        => SourceAnimationSelectionResult.Fail("Not used by this fixture.");
}
