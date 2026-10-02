using System;
using System.Collections.Generic;
using Dancy.Pap;

namespace Dancy.Services;

/// <summary>
/// The only target-inspection adapter that touches Dalamud game data. Its
/// methods are called exclusively by TargetInspectionService.Update.
/// </summary>
internal sealed class DalamudTargetInspectionDataSource : ITargetInspectionDataSource
{
    public IIncrementalTargetPathResolution BeginResolution(string timelineKey)
        => new PapResolutionAdapter(PapResolver.BeginResolution(timelineKey));

    public byte[] ReadTargetPapBytes(string gamePath)
        => PapEditor.ReadTargetPapBytes(gamePath);

    private sealed class PapResolutionAdapter(PapResolver.PapResolution resolution) : IIncrementalTargetPathResolution
    {
        public bool IsCompleted => resolution.IsCompleted;
        public IReadOnlyList<string> Results => resolution.Results;
        public int FileExistsRequests => resolution.FileExistsRequests;
        public int FileExistsCacheHits => resolution.FileExistsCacheHits;
        public int UnderlyingFileExistsProbes => resolution.UnderlyingFileExistsProbes;
        public bool TryAdvance() => resolution.TryAdvance();
    }
}
