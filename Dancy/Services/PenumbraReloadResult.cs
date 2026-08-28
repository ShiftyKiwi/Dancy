using System;

namespace Dancy.Services;

public sealed class PenumbraReloadResult
{
    public bool Succeeded { get; init; }
    public string Actual { get; init; } = string.Empty;
}

public interface IPenumbraModReloader
{
    PenumbraReloadResult Reload(string modDirectory, string modName);
}

public static class PenumbraReloadCapture
{
    public static PenumbraReloadResult Execute(Func<string> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var response = request();
            return new PenumbraReloadResult
            {
                Succeeded = string.Equals(response, "Success", StringComparison.OrdinalIgnoreCase),
                Actual = response,
            };
        }
        catch (Exception exception)
        {
            return new PenumbraReloadResult
            {
                Succeeded = false,
                Actual = $"{exception.GetType().Name}: {exception.Message}",
            };
        }
    }
}
