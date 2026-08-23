using System.ComponentModel.DataAnnotations;

namespace Ago.Platform.Resilience;

/// <summary>
/// One named pipeline's circuit-breaker setting - the <c>CircuitBreaker</c> group under
/// <c>Resilience:{pipelineName}:*</c>. Opens once <see cref="FailureRatio"/> of at least
/// <see cref="MinimumThroughput"/> calls fail inside <see cref="SamplingDuration"/>; half-opens
/// after <see cref="BreakDuration"/> to probe recovery with a single call. As with
/// <see cref="ResilienceRetryOptions"/>, which exceptions count as a failure is a caller-supplied
/// delegate on <see cref="ResiliencePolicyBuilder.WithCircuitBreaker"/>, not a bound setting.
/// </summary>
public sealed class ResilienceCircuitBreakerOptions
{
    [Range(0.01, 1.0)]
    public double FailureRatio { get; set; }

    [Range(2, int.MaxValue)]
    public int MinimumThroughput { get; set; }

    // Polly's own CircuitBreakerStrategyOptions enforces a 500ms floor on both durations
    // (Polly.Utils.ValidationHelper, discovered by this project's own test hitting it) - mirrored
    // here so a bad Resilience:{name}:CircuitBreaker:* value fails at options-validation time with a
    // clear message, instead of only when the pipeline is first built.
    [Range(typeof(TimeSpan), "00:00:00.500", "1.00:00:00")]
    public TimeSpan SamplingDuration { get; set; }

    [Range(typeof(TimeSpan), "00:00:00.500", "1.00:00:00")]
    public TimeSpan BreakDuration { get; set; }
}
