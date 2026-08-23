using System.ComponentModel.DataAnnotations;

namespace Ago.Platform.Resilience;

/// <summary>
/// Bound from <c>Resilience:{pipelineName}:*</c> - one instance per named pipeline via .NET's named
/// options feature (<see cref="ServiceCollectionExtensions.AddResiliencePipelineOptions"/>), e.g.
/// <c>Resilience:Redis</c>, <c>Resilience:S3</c>, and <c>6-05</c>'s future <c>Resilience:Webhooks</c>.
/// Every group is optional - a pipeline that never retries (Redis: <c>resilience.md</c>'s row for it
/// lists only "short timeout, circuit breaker, fallback to cache miss") simply never binds a
/// <see cref="Retry"/> section and its module never calls
/// <see cref="ResiliencePolicyBuilder.WithRetry"/>; nothing here forces every named pipeline to use
/// every pattern.
/// </summary>
public sealed class ResiliencePipelineOptions : IValidatableObject
{
    public const string SectionPrefix = "Resilience";

    public ResilienceTimeoutOptions? Timeout { get; set; }

    public ResilienceRetryOptions? Retry { get; set; }

    public ResilienceCircuitBreakerOptions? CircuitBreaker { get; set; }

    public ResilienceBulkheadOptions? Bulkhead { get; set; }

    // [Range]/[Required] declared directly on this class's own properties would only ever validate
    // that a Timeout/Retry/etc. reference is non-null, never the numbers inside it -
    // System.ComponentModel.DataAnnotations.Validator does not recurse into a nested object's own
    // attributes on its own. IValidatableObject is the documented way to opt into that recursion
    // explicitly; Validator.TryValidateObject calls it automatically as the final step, so this still
    // runs from the single ValidateDataAnnotations().ValidateOnStart() call every other options class
    // in this codebase already uses (naming-and-structure.md) - no second validation mechanism to
    // remember.
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidateGroup(Timeout, nameof(Timeout)))
            yield return result;

        foreach (var result in ValidateGroup(Retry, nameof(Retry)))
            yield return result;

        foreach (var result in ValidateGroup(CircuitBreaker, nameof(CircuitBreaker)))
            yield return result;

        foreach (var result in ValidateGroup(Bulkhead, nameof(Bulkhead)))
            yield return result;
    }

    private static IEnumerable<ValidationResult> ValidateGroup(object? group, string groupName)
    {
        if (group is null)
            yield break;

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(group, new ValidationContext(group), results, validateAllProperties: true);

        foreach (var result in results)
        {
            yield return new ValidationResult(
                $"{groupName}: {result.ErrorMessage}",
                result.MemberNames.Select(member => $"{groupName}.{member}"));
        }
    }
}
