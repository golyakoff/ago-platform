using Ago.Platform.Resilience;
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
        var pipeline = new ResiliencePolicyBuilder()
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
        var pipeline = new ResiliencePolicyBuilder()
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
        var pipeline = new ResiliencePolicyBuilder()
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
        var pipeline = new ResiliencePolicyBuilder()
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
}
