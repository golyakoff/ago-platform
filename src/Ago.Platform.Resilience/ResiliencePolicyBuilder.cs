using System.Threading.RateLimiting;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;

namespace Ago.Platform.Resilience;

/// <summary>
/// The one place a <see cref="ResiliencePipelineBuilder"/> may be constructed in this codebase -
/// <c>Ago.Platform.Architecture.Tests</c>' <c>ResilienceLayeringTests</c> enforces it. Before this
/// package existed, <c>Ago.Platform.Caching.Redis</c> and <c>Ago.Platform.Storage.S3</c> each built
/// their own private <c>BuildResiliencePipeline()</c> - same shape (retry + timeout + circuit
/// breaker over Polly), built independently, no shared code, no bulkhead concept in either
/// (<c>6-01</c>). This builder is the dedup: a fluent wrapper over the four patterns
/// <c>resilience.md</c>'s boundary table names, so a new resilient boundary (<c>6-05</c>'s webhook
/// dispatcher) composes existing building blocks instead of writing a fourth one. Callers still get
/// back a plain Polly <see cref="ResiliencePipeline"/> and call <c>ExecuteAsync</c> on it exactly as
/// before - no port-signature change at any existing call site.
///
/// `7-02`: <paramref name="pipelineName"/> is what lets <see cref="WithCircuitBreaker"/>/
/// <see cref="WithBulkhead"/> tag their own instruments (<see cref="ResilienceMetrics"/>) by pipeline
/// without either method taking a separate name parameter of its own - every existing caller already
/// has this name at hand as its own private <c>PipelineName</c> constant
/// (`Ago.Platform.Caching.Redis`'s "Redis", `Ago.Platform.Storage.S3`'s "S3"), so threading it through
/// the constructor instead is a one-line change per call site, not a new concept.
/// </summary>
public sealed class ResiliencePolicyBuilder(string pipelineName)
{
    private readonly ResiliencePipelineBuilder _pipeline = new();

    /// <summary>Cancels an in-flight execution once <paramref name="options"/>'s
    /// <see cref="ResilienceTimeoutOptions.Duration"/> elapses.</summary>
    public ResiliencePolicyBuilder WithTimeout(ResilienceTimeoutOptions options)
    {
        _pipeline.AddTimeout(options.Duration);
        return this;
    }

    /// <summary>
    /// Retries up to <see cref="ResilienceRetryOptions.MaxRetryAttempts"/> times with
    /// <see cref="ResilienceRetryOptions.BackoffType"/> backoff and
    /// <see cref="ResilienceRetryOptions.Delay"/> as the base delay.
    /// <paramref name="shouldHandle"/> is deliberately a parameter, not something read off
    /// <paramref name="options"/>: which exceptions are worth retrying is a judgment about the
    /// specific boundary, not a bound number.
    /// </summary>
    public ResiliencePolicyBuilder WithRetry(ResilienceRetryOptions options, Func<Exception, bool> shouldHandle)
    {
        _pipeline.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = options.MaxRetryAttempts,
            BackoffType = options.BackoffType,
            Delay = options.Delay,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(shouldHandle),
        });
        return this;
    }

    /// <summary>
    /// Opens once <see cref="ResilienceCircuitBreakerOptions.FailureRatio"/> of at least
    /// <see cref="ResilienceCircuitBreakerOptions.MinimumThroughput"/> calls fail within
    /// <see cref="ResilienceCircuitBreakerOptions.SamplingDuration"/>; half-opens after
    /// <see cref="ResilienceCircuitBreakerOptions.BreakDuration"/> to probe recovery with a single
    /// call. Same reasoning as <see cref="WithRetry"/> for why <paramref name="shouldHandle"/> is a
    /// parameter rather than a bound setting.
    /// </summary>
    public ResiliencePolicyBuilder WithCircuitBreaker(ResilienceCircuitBreakerOptions options, Func<Exception, bool> shouldHandle)
    {
        // `7-02`: a live CircuitBreakerStateProvider, not an OnOpened/OnClosed/OnHalfOpened callback
        // trio - Polly's own StateProvider always reflects the strategy's actual current state, so
        // ResilienceMetrics's gauge callback reads it directly rather than this class maintaining a
        // second, shadow copy of state transitions that could itself drift from the real thing.
        var stateProvider = new CircuitBreakerStateProvider();
        _pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = options.FailureRatio,
            MinimumThroughput = options.MinimumThroughput,
            SamplingDuration = options.SamplingDuration,
            BreakDuration = options.BreakDuration,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(shouldHandle),
            StateProvider = stateProvider,
        });
        ResilienceMetrics.RegisterBreaker(pipelineName, stateProvider);
        return this;
    }

    /// <summary>
    /// Bounds concurrent executions to <see cref="ResilienceBulkheadOptions.MaxConcurrency"/>;
    /// once <see cref="ResilienceBulkheadOptions.MaxQueuedActions"/> waiting callers is also
    /// exhausted, further calls are rejected with
    /// <see cref="Polly.RateLimiting.RateLimiterRejectedException"/>. Built on Polly v8's
    /// rate-limiter strategy (the <c>Polly.RateLimiting</c> package, wrapping
    /// <see cref="System.Threading.RateLimiting.ConcurrencyLimiter"/>) rather than the classic
    /// <c>Polly.Bulkhead</c> package: <c>Directory.Packages.props</c> already pins the lean v8
    /// <c>Polly.Core</c>, which does not ship the classic bulkhead API, and the rate-limiter
    /// strategy is that major version's supported replacement for the same pattern - confirmed
    /// against the pinned <c>Polly.Core</c> version rather than assumed (<c>6-01</c>'s open
    /// question).
    /// </summary>
    public ResiliencePolicyBuilder WithBulkhead(ResilienceBulkheadOptions options)
    {
        // `7-02`: the full RateLimiterStrategyOptions form, not the AddConcurrencyLimiter(int, int)
        // convenience overload this method used before - that overload builds the same
        // ConcurrencyLimiter internally (via DefaultRateLimiterOptions, reproduced explicitly here)
        // but exposes no OnRejected hook, and a bulkhead-rejection counter is exactly what this item
        // adds. Verified directly against the pinned Polly.RateLimiting 8.5.2 API before relying on
        // it (CLAUDE.md: measure, do not guess) - RateLimiter left null so the strategy builds its own
        // ConcurrencyLimiter from DefaultRateLimiterOptions, the same as the convenience overload did.
        _pipeline.AddRateLimiter(new RateLimiterStrategyOptions
        {
            DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
            {
                PermitLimit = options.MaxConcurrency,
                QueueLimit = options.MaxQueuedActions,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            },
            OnRejected = _ =>
            {
                ResilienceMetrics.RecordBulkheadRejection(pipelineName);
                return default;
            },
        });
        return this;
    }

    public ResiliencePipeline Build() => _pipeline.Build();
}
