using System;

namespace Dancy.Services;

public sealed class PenumbraReloadResult
{
    public bool Succeeded { get; init; }
    public string Actual { get; init; } = string.Empty;
    public bool Threw { get; init; }
    public bool Retried { get; init; }
    public int AttemptCount { get; init; } = 1;
    public string? InitialFailure { get; init; }

    public string UserFacingOutcome
        => Succeeded
            ? Retried
                ? $"confirmed after one retry (first attempt: {InitialFailure})"
                : "confirmed"
            : Retried
                ? $"failed after one retry (first attempt: {InitialFailure}; final attempt: {Actual})"
                : Actual;
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
                Threw = true,
            };
        }
    }

    /// <summary>
    /// ReloadMod is idempotent. A single retry is appropriate only when invoking
    /// the supported IPC threw before it produced a response; ordinary responses
    /// such as NotConnected are surfaced directly instead of being retried.
    /// </summary>
    public static PenumbraReloadResult ExecuteWithSingleRetry(Func<string> request)
    {
        var first = Execute(request);
        if (first.Succeeded || !first.Threw)
            return first;

        var retry = Execute(request);
        return new PenumbraReloadResult
        {
            Succeeded = retry.Succeeded,
            Actual = retry.Actual,
            Threw = retry.Threw,
            Retried = true,
            AttemptCount = 2,
            InitialFailure = first.Actual,
        };
    }
}
