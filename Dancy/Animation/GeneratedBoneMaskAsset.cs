using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dancy.Persistence;

namespace Dancy.Animation;

public sealed class GeneratedBoneMaskAssetIdentityInput
{
    public string SourcePapSha256 { get; init; } = string.Empty;
    public string ReplacementGamePath { get; init; } = string.Empty;
    public string MotionIdentity { get; init; } = string.Empty;
    public string TargetSkeletonIdentity { get; init; } = string.Empty;
    public string WriterSchemaVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> ExcludedBoneNames { get; init; } = Array.Empty<string>();
}

public sealed class GeneratedBoneMaskAssetIdentity
{
    public string Key { get; init; } = string.Empty;
    public string SourcePapSha256 { get; init; } = string.Empty;
    public string ReplacementGamePath { get; init; } = string.Empty;
    public string MotionIdentity { get; init; } = string.Empty;
    public string TargetSkeletonIdentity { get; init; } = string.Empty;
    public string WriterSchemaVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> ExcludedBoneNames { get; init; } = Array.Empty<string>();
}

public static class GeneratedBoneMaskAssets
{
    public const string SchemaVersion = "bone-mask-asset-v1";

    public static GeneratedBoneMaskAssetIdentity CreateIdentity(GeneratedBoneMaskAssetIdentityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sourceHash = Normalize(input.SourcePapSha256);
        var replacementPath = NormalizeGamePath(input.ReplacementGamePath);
        var motion = Normalize(input.MotionIdentity);
        var skeleton = Normalize(input.TargetSkeletonIdentity);
        var writerVersion = Normalize(input.WriterSchemaVersion);
        if (string.IsNullOrWhiteSpace(sourceHash) || string.IsNullOrWhiteSpace(replacementPath)
            || string.IsNullOrWhiteSpace(motion) || string.IsNullOrWhiteSpace(writerVersion))
        {
            throw new ArgumentException("Source hash, replacement game path, motion identity, and writer schema version are required.", nameof(input));
        }

        var names = BoneMaskNames.Normalize(input.ExcludedBoneNames);
        var canonical = string.Join("\n", new[]
        {
            SchemaVersion,
            sourceHash,
            replacementPath,
            motion,
            skeleton,
            writerVersion,
            string.Join("\n", names),
        });
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new GeneratedBoneMaskAssetIdentity
        {
            Key = key,
            SourcePapSha256 = sourceHash,
            ReplacementGamePath = replacementPath,
            MotionIdentity = motion,
            TargetSkeletonIdentity = skeleton,
            WriterSchemaVersion = writerVersion,
            ExcludedBoneNames = names,
        };
    }

    public static string CreateOutputPath(string dancyOwnedRoot, GeneratedBoneMaskAssetIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dancyOwnedRoot);
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Key.Length != 64 || identity.Key.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Generated asset identity must contain a SHA-256 key.", nameof(identity));

        var relativePath = Path.Combine("generated", "bone-masks", identity.Key[..2], identity.Key, "animation.pap");
        if (!PathSafety.TryResolveInsideRoot(dancyOwnedRoot, relativePath, out var outputPath))
            throw new InvalidOperationException("Dancy refused an output path outside its generated-asset root.");
        return outputPath;
    }

    private static string Normalize(string value) => value?.Trim() ?? string.Empty;

    private static string NormalizeGamePath(string value)
        => Normalize(value).Replace('\\', '/').ToLowerInvariant();
}
