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
        Assert.Equal("InvalidOperationException: IPC unavailable", result.Actual);
    }
}
