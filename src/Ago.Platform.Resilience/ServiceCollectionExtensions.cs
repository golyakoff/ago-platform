using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Platform.Resilience;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <c>Resilience:{pipelineName}:*</c> as a named
    /// <see cref="ResiliencePipelineOptions"/> instance -
    /// <c>naming-and-structure.md</c>'s options-binding convention (a section name plus
    /// <c>ValidateDataAnnotations().ValidateOnStart()</c>), extended with .NET's named-options
    /// feature so two different pipelines (Redis's, S3's, and later the webhook dispatcher's) can
    /// share the same <see cref="ResiliencePipelineOptions"/> shape and coexist as separate
    /// registrations in one <see cref="IServiceCollection"/> instead of colliding on one unqualified
    /// <see cref="IOptions{TOptions}"/>. <paramref name="configureDefaults"/> lets the calling
    /// module pre-populate the values it used to hardcode inside its own private
    /// <c>BuildResiliencePipeline()</c> before this package existed; configuration under the bound
    /// section overrides only the keys actually present there; anything absent keeps the
    /// pre-populated default.
    /// </summary>
    public static OptionsBuilder<ResiliencePipelineOptions> AddResiliencePipelineOptions(
        this IServiceCollection services,
        string pipelineName,
        IConfiguration configuration,
        Action<ResiliencePipelineOptions>? configureDefaults = null)
    {
        var builder = services.AddOptions<ResiliencePipelineOptions>(pipelineName);

        if (configureDefaults is not null)
            builder.Configure(configureDefaults);

        return builder
            .Bind(configuration.GetSection($"{ResiliencePipelineOptions.SectionPrefix}:{pipelineName}"))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
