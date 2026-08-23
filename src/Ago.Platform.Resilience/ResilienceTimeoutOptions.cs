using System.ComponentModel.DataAnnotations;

namespace Ago.Platform.Resilience;

/// <summary>
/// One named pipeline's timeout duration - the whole of the <c>Timeout</c> group under
/// <c>Resilience:{pipelineName}:*</c> (<see cref="ResiliencePipelineOptions"/>). No built-in default:
/// the module wiring a named pipeline (<c>Ago.Platform.Caching.Redis</c>,
/// <c>Ago.Platform.Storage.S3</c>) pre-configures the value it used to hardcode inside its own
/// private <c>BuildResiliencePipeline()</c> before this package existed, and configuration overrides
/// it from there - a one-size-fits-all default here would be exactly the invented number
/// <c>CLAUDE.md</c> rules out ("do not invent numbers, benchmarks... measure or stay silent").
/// </summary>
public sealed class ResilienceTimeoutOptions
{
    [Range(typeof(TimeSpan), "00:00:00.001", "1.00:00:00")]
    public TimeSpan Duration { get; set; }
}
