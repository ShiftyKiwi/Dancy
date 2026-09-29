using System;
using System.Collections.Generic;
using System.Linq;
using Dancy.Core.Models;

namespace Dancy.Pap;

public enum SourceAnimationSelectionMethod
{
    SingleMotion,
    CompanionTimelineEvent,
}

/// <summary>
/// The one complete animation unit Dancy may copy from a source PAP. A selector
/// is required for multi-motion banks; Dancy never guesses a header or timeline.
/// </summary>
public sealed record SourceAnimationSelection(
    string LogicalGamePath,
    string PhysicalPapPath,
    int AnimationHeaderIndex,
    string AnimationEvent,
    int HavokMotionIndex,
    int EmbeddedTmbIndex,
    SourceAnimationSelectionMethod Method,
    string Evidence);

public sealed record SourceAnimationSelectionResult(SourceAnimationSelection? Selection, string? Error)
{
    public bool IsSuccess => Selection is not null && string.IsNullOrWhiteSpace(Error);

    public static SourceAnimationSelectionResult Success(SourceAnimationSelection selection)
        => new(selection, null);

    public static SourceAnimationSelectionResult Fail(string error)
        => new(null, error);
}

/// <summary>
/// Resolves one source motion from PAP structure plus explicit option-local
/// companion timeline evidence. It deliberately has no filename or option-name
/// fallback for multi-motion source PAPs.
/// </summary>
public static class SourceAnimationSelector
{
    public static SourceAnimationSelectionResult Select(
        string logicalGamePath,
        string physicalPapPath,
        PapFileInspector.PapFileInspection inspection,
        IReadOnlyList<CompanionTimelineOverride> companionTimelines,
        Func<CompanionTimelineOverride, IReadOnlyList<string>> readCompanionEvents,
        IReadOnlyList<IReadOnlyList<string>> embeddedTimelineEvents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalGamePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPapPath);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(companionTimelines);
        ArgumentNullException.ThrowIfNull(readCompanionEvents);
        ArgumentNullException.ThrowIfNull(embeddedTimelineEvents);

        if (!HasCoherentStructure(inspection, embeddedTimelineEvents, out var structureError))
            return SourceAnimationSelectionResult.Fail(structureError);

        if (inspection.AnimationCount == 1)
        {
            return SourceAnimationSelectionResult.Success(new SourceAnimationSelection(
                logicalGamePath,
                physicalPapPath,
                0,
                inspection.AnimationNames[0],
                inspection.HavokIndices[0],
                0,
                SourceAnimationSelectionMethod.SingleMotion,
                "The source PAP contains one animation header and one embedded timeline."));
        }

        var headerIndexesByEvent = inspection.AnimationNames
            .Select((name, index) => (Name: name, Index: index))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Name))
            .GroupBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Index).ToList(), StringComparer.OrdinalIgnoreCase);
        if (headerIndexesByEvent.Count != inspection.AnimationCount || headerIndexesByEvent.Values.Any(indexes => indexes.Count != 1))
        {
            return SourceAnimationSelectionResult.Fail(
                "This source PAP contains multiple animations, and Dancy could not determine one unique animation event for each header.");
        }

        var candidates = new List<(CompanionTimelineOverride Timeline, string Event, int HeaderIndex)>();
        foreach (var timeline in companionTimelines.Where(timeline => timeline.IsActionTimeline))
        {
            var matchingEvents = readCompanionEvents(timeline)
                .Where(eventName => headerIndexesByEvent.ContainsKey(eventName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matchingEvents.Count > 1)
            {
                return SourceAnimationSelectionResult.Fail(
                    "This source option's companion timeline references multiple animations in the shared PAP.");
            }

            if (matchingEvents.Count == 1)
                candidates.Add((timeline, matchingEvents[0], headerIndexesByEvent[matchingEvents[0]][0]));
        }

        if (candidates.Count == 0)
        {
            return SourceAnimationSelectionResult.Fail(
                "This source PAP contains multiple animations, and Dancy could not determine which animation this mod option uses.");
        }

        if (candidates.Count != 1)
        {
            return SourceAnimationSelectionResult.Fail(
                "This source option has multiple companion timelines that select animations in the shared PAP.");
        }

        var candidate = candidates[0];
        if (!embeddedTimelineEvents[candidate.HeaderIndex]
                .Any(eventName => string.Equals(eventName, candidate.Event, StringComparison.OrdinalIgnoreCase)))
        {
            return SourceAnimationSelectionResult.Fail(
                "The companion timeline selected an animation, but the corresponding embedded PAP timeline did not contain the same event.");
        }

        return SourceAnimationSelectionResult.Success(new SourceAnimationSelection(
            logicalGamePath,
            physicalPapPath,
            candidate.HeaderIndex,
            candidate.Event,
            inspection.HavokIndices[candidate.HeaderIndex],
            candidate.HeaderIndex,
            SourceAnimationSelectionMethod.CompanionTimelineEvent,
            $"Companion timeline {candidate.Timeline.GamePath} selected event {candidate.Event}."));
    }

    private static bool HasCoherentStructure(
        PapFileInspector.PapFileInspection inspection,
        IReadOnlyList<IReadOnlyList<string>> embeddedTimelineEvents,
        out string error)
    {
        if (inspection.AnimationCount <= 0
            || inspection.AnimationNames.Count != inspection.AnimationCount
            || inspection.HavokIndices.Count != inspection.AnimationCount
            || inspection.TimelineSectionSizes.Count != inspection.AnimationCount
            || embeddedTimelineEvents.Count != inspection.AnimationCount)
        {
            error = "This source PAP has an incomplete animation-header, Havok, or embedded-timeline relationship.";
            return false;
        }

        if (inspection.HavokIndices.Any(index => index < 0))
        {
            error = "This source PAP contains an animation header with an invalid Havok motion binding.";
            return false;
        }

        if (inspection.AnimationNames.Any(string.IsNullOrWhiteSpace))
        {
            error = "This source PAP contains an animation header without an event identifier.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
