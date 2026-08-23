using System.ComponentModel.DataAnnotations;
using Polly;

namespace Ago.Platform.Resilience;

/// <summary>
/// One named pipeline's retry setting - the <c>Retry</c> group under
/// <c>Resilience:{pipelineName}:*</c>. Which exceptions are worth retrying is deliberately not part
/// of this shape: that is a judgment about the specific boundary
/// (<c>resilience.md</c>'s "retrying non-idempotent operations blindly" warning - retry is only safe
/// where the call is idempotent or carries an idempotency key), not a number a config file can
/// express, so <see cref="ResiliencePolicyBuilder.WithRetry"/> takes it as a caller-supplied
/// delegate instead.
/// </summary>
public sealed class ResilienceRetryOptions
{
    [Range(0, 20)]
    public int MaxRetryAttempts { get; set; }

    [Range(typeof(TimeSpan), "00:00:00", "00:01:00")]
    public TimeSpan Delay { get; set; }

    public DelayBackoffType BackoffType { get; set; } = DelayBackoffType.Exponential;
}
