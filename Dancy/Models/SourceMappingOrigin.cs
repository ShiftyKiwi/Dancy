namespace Dancy.Core.Models;

/// <summary>
/// Records whether a logical source path came from the selected Penumbra option
/// or was explicitly added by the user for this override only.
/// </summary>
public enum SourceMappingOrigin
{
    ModProvided,
    UserAddedCompatible,
}

public static class SourceMappingOriginExtensions
{
    public static string DisplayName(this SourceMappingOrigin origin)
        => origin switch
        {
            SourceMappingOrigin.ModProvided => "Source-provided mapping",
            SourceMappingOrigin.UserAddedCompatible => "User-added compatible mapping",
            _ => origin.ToString(),
        };
}
