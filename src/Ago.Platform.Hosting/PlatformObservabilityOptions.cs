using System.ComponentModel.DataAnnotations;

namespace Ago.Platform.Hosting;

/// <summary>
/// Bound from <c>Otel:*</c> (naming-and-structure.md's options-validation rule: a typo in a key
/// must fail the pod, not silently disable a feature - here that would mean shipping traces nobody
/// receives). <see cref="ServiceName"/> is deliberately not bound from configuration: it is passed
/// as a parameter to <see cref="ServiceCollectionExtensions.AddPlatformObservability"/> instead
/// (each host's own <c>Program.cs</c> literally states its own name, `Ago.Chat.Api`/`Worker`/
/// `Webhooks`, the same way <c>ChatModule.Name</c> does) - three near-identical
/// <c>appsettings.json</c> files differing in one string is a worse failure mode (copy-paste the
/// wrong one, ship two hosts both claiming to be `Ago.Chat.Api`) than a value the host's own source
/// already unambiguously knows.
/// </summary>
public sealed class PlatformObservabilityOptions : IValidatableObject
{
    public const string SectionName = "Otel";

    /// <summary>Falls back to "0.0.0" (the same "unmeasured starting point, stated as such" shape
    /// as every other unversioned default in this codebase) rather than failing startup - a missing
    /// version should not be worse than an unhelpful one in a trace.</summary>
    public string ServiceVersion { get; set; } = "0.0.0";

    public string DeploymentEnvironment { get; set; } = "local";

    public OtelExporterOptions Exporter { get; set; } = new();

    // Same reasoning as ResiliencePipelineOptions' own IValidatableObject: [Required] on this
    // class's own properties would only ever check that Exporter is non-null, never recurse into
    // its own [Required] Endpoint - Validator.TryValidateObject does not walk nested objects on its
    // own, IValidatableObject is the documented opt-in, and Validator.TryValidateObject calls it
    // automatically as the final step of the same ValidateDataAnnotations().ValidateOnStart() every
    // other options class here already uses.
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(Exporter, new ValidationContext(Exporter), results, validateAllProperties: true);
        foreach (var result in results)
        {
            yield return new ValidationResult(
                $"{nameof(Exporter)}: {result.ErrorMessage}",
                result.MemberNames.Select(member => $"{nameof(Exporter)}.{member}"));
        }
    }
}

/// <summary>Bound from <c>Otel:Exporter:*</c> - the backlog's own config key, <c>Otel:Exporter:Endpoint</c>.</summary>
public sealed class OtelExporterOptions
{
    [Required]
    public Uri? Endpoint { get; set; }
}
