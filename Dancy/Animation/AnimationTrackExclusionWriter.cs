using System;
using System.Collections.Generic;
using System.Linq;

namespace Dancy.Animation;

public interface IAnimationTrackExclusionWriter
{
    string BackendId { get; }
    AnimationTrackExclusionWriteResult RebuildWithExcludedBones(AnimationTrackExclusionWriteRequest request);
}

public sealed class AnimationTrackExclusionWriteRequest
{
    public string SourcePapPath { get; init; } = string.Empty;
    public string DancyOwnedOutputPath { get; init; } = string.Empty;
    public BoneMaskAnimationAssociation Association { get; init; } = new();
    public GeneratedBoneMaskAssetIdentity AssetIdentity { get; init; } = new();
    public BoneMaskExclusionPlan ExclusionPlan { get; init; } = new();
}

public sealed class AnimationTrackExclusionStructuralValidation
{
    public bool PapParsed { get; init; }
    public bool HkxParsed { get; init; }
    public bool BindingsValid { get; init; }
    public bool ExpectedTracksRemoved { get; init; }
    public bool NoUnexpectedMappings { get; init; }
    public bool ReconstructedRetainedTracks { get; init; }
    public bool RetainedTracksSemanticallyEquivalent { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool IsValid => PapParsed && HkxParsed && BindingsValid && ExpectedTracksRemoved && NoUnexpectedMappings
                           && (!ReconstructedRetainedTracks || RetainedTracksSemanticallyEquivalent);
}

public sealed class AnimationTrackExclusionWriteResult
{
    public bool Succeeded { get; init; }
    public string BackendId { get; init; } = string.Empty;
    public string GeneratedAssetKey { get; init; } = string.Empty;
    public string OutputPapPath { get; init; } = string.Empty;
    public string OutputPapSha256 { get; init; } = string.Empty;
    public int SourceTrackCount { get; init; }
    public int OutputTrackCount { get; init; }
    public IReadOnlyList<string> ExcludedBoneNames { get; init; } = Array.Empty<string>();
    public AnimationTrackExclusionStructuralValidation? Validation { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}

public sealed class UnavailableAnimationTrackExclusionWriter : IAnimationTrackExclusionWriter
{
    public const string UnavailableErrorCode = "WriterUnavailable";
    public string BackendId => "unavailable";

    public AnimationTrackExclusionWriteResult RebuildWithExcludedBones(AnimationTrackExclusionWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AnimationTrackExclusionWriteResult
        {
            Succeeded = false,
            BackendId = BackendId,
            GeneratedAssetKey = request.AssetIdentity.Key,
            ErrorCode = UnavailableErrorCode,
            Error = "No production animation track-exclusion writer is available. Install a supported writer backend before generating this Bone Mask asset.",
        };
    }
}

public static class AnimationTrackExclusionResultValidator
{
    public static IReadOnlyList<string> Validate(AnimationTrackExclusionWriteRequest request, AnimationTrackExclusionWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var failures = new List<string>();
        if (!result.Succeeded)
        {
            failures.Add(result.ErrorCode ?? "The writer did not report a successful result.");
            return failures;
        }

        if (!string.Equals(request.AssetIdentity.Key, result.GeneratedAssetKey, StringComparison.OrdinalIgnoreCase))
            failures.Add("The generated asset identity does not match the request.");
        if (string.IsNullOrWhiteSpace(result.OutputPapPath))
            failures.Add("The writer did not report a generated PAP path.");
        if (string.IsNullOrWhiteSpace(result.OutputPapSha256))
            failures.Add("The writer did not report a generated PAP SHA-256.");
        if (result.SourceTrackCount < 0 || result.OutputTrackCount < 0 || result.OutputTrackCount > result.SourceTrackCount)
            failures.Add("The reported transform-track counts are invalid.");

        var expectedNames = BoneMaskNames.Normalize(request.ExclusionPlan.ExcludedTracks.Select(track => track.BoneName));
        var reportedNames = BoneMaskNames.Normalize(result.ExcludedBoneNames);
        if (!expectedNames.SequenceEqual(reportedNames, StringComparer.OrdinalIgnoreCase))
            failures.Add("The writer did not report exactly the planned named exclusions.");
        if (result.Validation is null || !result.Validation.IsValid)
            failures.Add("The writer did not provide a complete structural validation result.");
        return failures;
    }
}
