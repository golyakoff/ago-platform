using Ago.Platform.Resilience;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Ago.Platform.Tests;

/// <summary>
/// Each of the four patterns in isolation, with Polly's own fault injection (a delegate that throws
/// or blocks on demand) rather than a real network - `6-01`'s Done-when. These exercise
/// <see cref="ResiliencePolicyBuilder"/> directly, the same object
/// `Ago.Platform.Caching.Redis`/`Ago.Platform.Storage.S3` build against, not
/// <c>Polly.ResiliencePipelineBuilder</c> itself - proving the wrapper actually wires each pattern
/// through, not just that Polly works.
/// </summary>
public class ResiliencePolicyBuilderTests
{
    [Fact]
    public async Task WithRetry_GivesUpAfterMaxAttempts_AndSurfacesTheLastFailure()
    {
        var attempts = 0;
        var pipeline = new ResiliencePolicyBuilder("test-pipeline")
            .WithRetry(
                new ResilienceRetryOptions { MaxRetryAttempts = 2, Delay = TimeSpan.FromMilliseconds(1) },
                static _ => true)
            .Build();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ExecuteAsync(_ =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("always fails");
            }).AsTask());

        Assert.Equal("always fails", thrown.Message);
        Assert.Equal(3, attempts); // the original call plus 2 retries, then give up
    }

    [Fact]
    public async Task WithTimeout_CancelsAnExecutionThatOutlivesTheDuration()
    {
        var pipeline = new ResiliencePolicyBuilder("test-pipeline")
            .WithTimeout(new ResilienceTimeoutOptions { Duration = TimeSpan.FromMilliseconds(50) })
            .Build();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            pipeline.ExecuteAsync(async ct => await Task.Delay(TimeSpan.FromSeconds(30), ct)).AsTask());
        stopwatch.Stop();

        // Proves it actually cancelled the delay rather than merely racing it - a delay this far
        // under the 30s the callback asked for could not have completed on its own.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected the timeout to cut the 30s delay short; took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task WithCircuitBreaker_OpensAfterTheFailureRatioIsMet_AndHalfOpensAfterBreakDuration()
    {
        var pipeline = new ResiliencePolicyBuilder("test-pipeline")
            .WithCircuitBreaker(
                new ResilienceCircuitBreakerOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 2,
                    SamplingDuration = TimeSpan.FromSeconds(1),
                    BreakDuration = TimeSpan.FromMilliseconds(500), // Polly's own floor for this option
                },
                static _ => true)
            .Build();

        // MinimumThroughput failing calls open the breaker.
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pipeline.ExecuteAsync(_ => throw new InvalidOperationException("boom")).AsTask());
        }

        // Open: the delegate is not even invoked - BrokenCircuitException comes from the breaker
        // itself, not from the (never-called) callback.
        var invokedWhileOpen = false;
        await Assert.ThrowsAsync<BrokenCircuitException>(() =>
            pipeline.ExecuteAsync(_ =>
            {
                invokedWhileOpen = true;
                return default;
            }).AsTask());
        Assert.False(invokedWhileOpen);

        // Half-open after BreakDuration: the next call is let through as a probe, and a success
        // closes the breaker again.
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        var probeRan = false;
        await pipeline.ExecuteAsync(_ =>
        {
            probeRan = true;
            return default;
        });
        Assert.True(probeRan);
    }

    [Fact]
    public async Task WithBulkhead_RejectsCallsBeyondTheConcurrencyLimitAndQueue()
    {
        var pipeline = new ResiliencePolicyBuilder("test-pipeline")
            .WithBulkhead(new ResilienceBulkheadOptions { MaxConcurrency = 1, MaxQueuedActions = 0 })
            .Build();

        var holdFirstCall = new TaskCompletionSource();
        var firstCallStarted = new TaskCompletionSource();
        var firstCall = pipeline.ExecuteAsync(async _ =>
        {
            firstCallStarted.SetResult();
            await holdFirstCall.Task;
        }).AsTask();

        await firstCallStarted.Task;

        // The single permit is held by the first call and there is no queue slot, so a second
        // concurrent call is rejected immediately rather than waiting for the first to finish.
        await Assert.ThrowsAsync<RateLimiterRejectedException>(() =>
            pipeline.ExecuteAsync(_ => default).AsTask());

        holdFirstCall.SetResult();
        await firstCall;
    }

    /// <summary>
    /// `7-02`'s Done-when: "prove at least one real value change per instrument", not merely that it
    /// was registered. Reads the actual metric point OpenTelemetry's own in-memory reader captured -
    /// the same "resolve the real collaborator" bar `PlatformObservabilityTests` already holds itself
    /// to for tracing.
    /// </summary>
    [Fact]
    public async Task WithCircuitBreaker_BreakerStateGauge_MovesFromClosedToOpen()
    {
        var pipelineName = $"test-breaker-{Guid.NewGuid():N}";
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ResilienceMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        var pipeline = new ResiliencePolicyBuilder(pipelineName)
            .WithCircuitBreaker(
                new ResilienceCircuitBreakerOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 2,
                    SamplingDuration = TimeSpan.FromSeconds(1),
                    BreakDuration = TimeSpan.FromMilliseconds(500),
                },
                static _ => true)
            .Build();

        Assert.Equal("closed", ReadCurrentState(meterProvider, exportedMetrics, pipelineName));

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pipeline.ExecuteAsync(_ => throw new InvalidOperationException("boom")).AsTask());
        }

        Assert.Equal("open", ReadCurrentState(meterProvider, exportedMetrics, pipelineName));
    }

    [Fact]
    public async Task WithBulkhead_RejectedCallsAreCounted_TaggedByPipelineName()
    {
        var pipelineName = $"test-bulkhead-{Guid.NewGuid():N}";
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ResilienceMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        var pipeline = new ResiliencePolicyBuilder(pipelineName)
            .WithBulkhead(new ResilienceBulkheadOptions { MaxConcurrency = 1, MaxQueuedActions = 0 })
            .Build();

        var holdFirstCall = new TaskCompletionSource();
        var firstCallStarted = new TaskCompletionSource();
        var firstCall = pipeline.ExecuteAsync(async _ =>
        {
            firstCallStarted.SetResult();
            await holdFirstCall.Task;
        }).AsTask();
        await firstCallStarted.Task;

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() =>
            pipeline.ExecuteAsync(_ => default).AsTask());

        holdFirstCall.SetResult();
        await firstCall;

        meterProvider.ForceFlush();
        var rejections = exportedMetrics.Single(m => m.Name == ResilienceMetrics.BulkheadRejectionsInstrumentName);
        var total = SumLongPoints(rejections, pipelineName);
        Assert.Equal(1, total);
    }

    private static string ReadCurrentState(MeterProvider meterProvider, List<Metric> exportedMetrics, string pipelineName)
    {
        exportedMetrics.Clear();
        meterProvider.ForceFlush();
        var breakerState = exportedMetrics.Single(m => m.Name == ResilienceMetrics.CircuitBreakerStateInstrumentName);

        foreach (ref readonly var point in breakerState.GetMetricPoints())
        {
            string? pipeline = null;
            string? state = null;
            foreach (var tag in point.Tags)
            {
                if (tag.Key == "pipeline")
                {
                    pipeline = (string?)tag.Value;
                }
                else if (tag.Key == "state")
                {
                    state = (string?)tag.Value;
                }
            }

            if (pipeline == pipelineName && point.GetGaugeLastValueLong() == 1 && state is not null)
            {
                return state;
            }
        }

        throw new InvalidOperationException($"No 'current' state point found for pipeline {pipelineName}.");
    }

    private static long SumLongPoints(Metric metric, string pipelineName)
    {
        long total = 0;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key == "pipeline" && (string?)tag.Value == pipelineName)
                {
                    total += point.GetSumLong();
                }
            }
        }

        return total;
    }
}
