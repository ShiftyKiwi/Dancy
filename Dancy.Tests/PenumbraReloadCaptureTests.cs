using System;
using Dancy.Services;
using Xunit;

namespace Dancy.Tests;

public class PenumbraReloadCaptureTests
{
    [Fact]
    public void ReportsPenumbraResponseFailureWithoutThrowing()
    {
        var result = PenumbraReloadCapture.Execute(() => "NotConnected");

        Assert.False(result.Succeeded);
        Assert.Equal("NotConnected", result.Actual);
    }

    [Fact]
    public void ReportsPenumbraExceptionWithoutThrowing()
    {
        var result = PenumbraReloadCapture.Execute(() => throw new InvalidOperationException("IPC unavailable"));

        Assert.False(result.Succeeded);
        Assert.True(result.Threw);
        Assert.Equal("InvalidOperationException: IPC unavailable", result.Actual);
    }

    [Fact]
    public void RetriesOneThrownReloadAndReportsTheSuccessfulRetry()
    {
        var attempts = 0;

        var result = PenumbraReloadCapture.ExecuteWithSingleRetry(() =>
        {
            attempts++;
            if (attempts == 1)
                throw new InvalidOperationException("transient IPC failure");
            return "Success";
        });

        Assert.True(result.Succeeded);
        Assert.True(result.Retried);
        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, attempts);
        Assert.Equal("InvalidOperationException: transient IPC failure", result.InitialFailure);
        Assert.Equal("confirmed after one retry (first attempt: InvalidOperationException: transient IPC failure)", result.UserFacingOutcome);
    }

    [Fact]
    public void DoesNotRetryAnOrdinaryPenumbraFailureResponse()
    {
        var attempts = 0;

        var result = PenumbraReloadCapture.ExecuteWithSingleRetry(() =>
        {
            attempts++;
            return "NotConnected";
        });

        Assert.False(result.Succeeded);
        Assert.False(result.Retried);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(1, attempts);
        Assert.Equal("NotConnected", result.UserFacingOutcome);
    }

    [Fact]
    public void StopsAfterOneRetryWhenTheReloadKeepsThrowing()
    {
        var attempts = 0;

        var result = PenumbraReloadCapture.ExecuteWithSingleRetry(() =>
        {
            attempts++;
            throw new InvalidOperationException($"failure {attempts}");
        });

        Assert.False(result.Succeeded);
        Assert.True(result.Retried);
        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, attempts);
        Assert.Equal("InvalidOperationException: failure 1", result.InitialFailure);
        Assert.Equal("failed after one retry (first attempt: InvalidOperationException: failure 1; final attempt: InvalidOperationException: failure 2)", result.UserFacingOutcome);
    }
}
