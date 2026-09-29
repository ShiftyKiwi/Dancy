using System;
using System.IO;
using System.Linq;
using Dancy.Domain;
using Dancy.Persistence;

namespace Dancy.Pap;

/// <summary>
/// Resolves a plan copy's one source animation unit from local Penumbra files.
/// This is intentionally the only production bridge from option metadata to a
/// selector-backed multi-motion source.
/// </summary>
public static class SourceAnimationSelectionResolver
{
    public static SourceAnimationSelectionResult Resolve(string modFolder, PlannedPapCopy copy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        ArgumentNullException.ThrowIfNull(copy);

        if (copy.SourceGamePaths.Count == 0)
            return SourceAnimationSelectionResult.Fail("The selected source PAP has no logical game-path mapping.");
        if (!PathSafety.TryResolveInsideRoot(modFolder, copy.SourcePapPath, out var sourcePath) || !File.Exists(sourcePath))
            return SourceAnimationSelectionResult.Fail("The selected source PAP is missing or outside the selected Penumbra mod.");

        try
        {
            var inspection = PapFileInspector.InspectFile(sourcePath);
            if (inspection.AnimationCount > 1 && copy.SourceOptionPhysicalPapCount != 1)
            {
                return SourceAnimationSelectionResult.Fail(
                    "This source option maps to multiple physical PAP files. Dancy requires one shared PAP before it can select a multi-motion source safely.");
            }
            var embeddedEvents = PapEditor.ReadEmbeddedTimelineEventIdentifiers(sourcePath);
            return SourceAnimationSelector.Select(
                copy.SourceGamePaths[0],
                copy.SourcePapPath,
                inspection,
                copy.CompanionTimelines,
                timeline => ReadCompanionEvents(modFolder, timeline),
                embeddedEvents);
        }
        catch (Exception exception)
        {
            return SourceAnimationSelectionResult.Fail($"Dancy could not inspect the source option's companion timeline evidence: {exception.Message}");
        }
    }

    private static System.Collections.Generic.IReadOnlyList<string> ReadCompanionEvents(
        string modFolder,
        Core.Models.CompanionTimelineOverride timeline)
    {
        if (!PathSafety.TryResolveInsideRoot(modFolder, timeline.ModdedTimelinePath, out var timelinePath) || !File.Exists(timelinePath))
            throw new FileNotFoundException("The selected source option's companion timeline is missing or unsafe.", timeline.ModdedTimelinePath);
        return PapEditor.ReadCompanionTimelineEventIdentifiers(timelinePath);
    }
}
