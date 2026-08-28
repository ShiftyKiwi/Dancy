using Dancy.Core.Models;
namespace Dancy.Core;

public static class EmotePathResolver
{
    public static ResolvedEmoteInfo? Resolve(string gamePath)
        => EmoteLibrary.ResolveTimeline(gamePath);
}
