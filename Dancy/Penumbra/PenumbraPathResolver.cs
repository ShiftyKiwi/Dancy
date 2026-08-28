using System;
using Dancy.Core;

namespace Dancy.Penumbra
{
    public static class PenumbraPathResolver
    {
        /// <summary>
        /// Attempts to read Penumbra's configuration and extract the ModDirectory path.
        /// Returns null if not found or invalid.
        /// </summary>
        public static string? ResolvePenumbraModDirectory(string dalamudConfigDirectory)
            => PenumbraDirectoryResolver.GetPenumbraDirectory(dalamudConfigDirectory);
    }
}
