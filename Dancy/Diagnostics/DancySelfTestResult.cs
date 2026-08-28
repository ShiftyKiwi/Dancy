#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;

namespace Dancy.Diagnostics;

public enum DancySelfTestStatus
{
    Passed,
    Failed,
    Skipped,
}

public sealed class DancySelfTestCase
{
    public string TestName { get; init; } = string.Empty;
    public DancySelfTestStatus Status { get; init; }
    public string Stage { get; init; } = string.Empty;
    public string Expected { get; init; } = string.Empty;
    public string Actual { get; init; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public long DurationMilliseconds { get; init; }
    public IReadOnlyList<string> Artifacts { get; init; } = Array.Empty<string>();
    public string? FailureReason { get; init; }
}

public sealed class DancySelfTestResult
{
    public string Schema { get; init; } = "dancy.selftest.v1";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public IReadOnlyList<DancySelfTestCase> Cases { get; init; } = Array.Empty<DancySelfTestCase>();
    public IReadOnlyList<string> RetainedArtifacts { get; init; } = Array.Empty<string>();
    public bool Passed => Cases.Count > 0 && Cases.All(test => test.Status != DancySelfTestStatus.Failed);
    public string Summary => $"{Cases.Count(test => test.Status == DancySelfTestStatus.Passed)}/{Cases.Count} PASS";
}
#endif
