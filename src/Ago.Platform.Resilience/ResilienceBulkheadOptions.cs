using System.ComponentModel.DataAnnotations;

namespace Ago.Platform.Resilience;

/// <summary>
/// One named pipeline's bulkhead setting - the <c>Bulkhead</c> group under
/// <c>Resilience:{pipelineName}:*</c>. <see cref="MaxConcurrency"/> executions run at once;
/// <see cref="MaxQueuedActions"/> more may wait for a slot before
/// <see cref="ResiliencePolicyBuilder.WithBulkhead"/>'s pipeline starts rejecting with
/// <see cref="Polly.RateLimiting.RateLimiterRejectedException"/>. Nothing in
/// <c>Ago.Platform.Caching.Redis</c> or <c>Ago.Platform.Storage.S3</c> uses this group today -
/// neither boundary's row in <c>resilience.md</c> calls for one - it exists so <c>6-05</c>'s webhook
/// dispatcher (the boundary that does need one, one shop's dead endpoint must not consume the whole
/// worker pool) composes it from here instead of writing a fourth ad hoc implementation.
/// </summary>
public sealed class ResilienceBulkheadOptions
{
    [Range(1, int.MaxValue)]
    public int MaxConcurrency { get; set; }

    [Range(0, int.MaxValue)]
    public int MaxQueuedActions { get; set; }
}
