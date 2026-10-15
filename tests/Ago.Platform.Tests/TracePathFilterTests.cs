using Ago.Platform.Observability;
using Microsoft.AspNetCore.Http;

namespace Ago.Platform.Tests;

/// <summary>`26-331`: probes and scrapes get no trace; every other path does.</summary>
public class TracePathFilterTests
{
    [Theory]
    [InlineData("/healthz/live")]
    [InlineData("/healthz/ready")]
    [InlineData("/healthz")]
    [InlineData("/HEALTHZ/Live")]
    [InlineData("/metrics")]
    public void ShouldTrace_ProbeOrScrape_IsFalse(string path) =>
        Assert.False(TracePathFilter.ShouldTrace(new PathString(path)));

    [Theory]
    [InlineData("/")]
    [InlineData("/api/v1/conversations")]
    [InlineData("/hubs/visitor/negotiate")]
    [InlineData("/healthzone")]
    [InlineData("/metrics-export")]
    [InlineData("/api/healthz")]
    public void ShouldTrace_AnythingElse_IsTrue(string path) =>
        Assert.True(TracePathFilter.ShouldTrace(new PathString(path)));

    [Fact]
    public void ShouldTrace_TakesTheRequestPathFromTheContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/healthz/ready";

        Assert.False(TracePathFilter.ShouldTrace(context));
    }
}
