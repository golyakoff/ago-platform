using System.ComponentModel.DataAnnotations;
using Ago.Platform.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Ago.Platform.Tests;

/// <summary>
/// `7-01`'s Done-when: "Ago.Platform.Hosting's new extension has its own unit tests (resource
/// attributes set correctly, config binding validated at startup - like every other options class
/// in this codebase already does)". Resolves the OTel SDK's own <see cref="TracerProvider"/> to
/// assert against - the same "test the real collaborator, not a private field" bar every other test
/// in this project holds itself to.
/// </summary>
public class PlatformObservabilityTests
{
    [Fact]
    public void AddPlatformObservability_SetsResourceAttributes_FromTheServiceNameParameterAndBoundOptions()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Otel:Exporter:Endpoint"] = "http://localhost:4317",
            ["Otel:ServiceVersion"] = "1.2.3",
            ["Otel:DeploymentEnvironment"] = "test",
        });

        var services = new ServiceCollection();
        services.AddPlatformObservability(configuration, "Ago.Chat.Api");
        using var provider = services.BuildServiceProvider();

        var tracerProvider = provider.GetRequiredService<TracerProvider>();
        var resource = tracerProvider.GetResource();
        var attributes = resource.Attributes.ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal("Ago.Chat.Api", attributes["service.name"]);
        Assert.Equal("1.2.3", attributes["service.version"]);
        Assert.Equal("test", attributes["deployment.environment"]);
    }

    [Fact]
    public void AddPlatformObservability_ServiceVersionAndEnvironment_DefaultWhenNotConfigured()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Otel:Exporter:Endpoint"] = "http://localhost:4317",
        });

        var services = new ServiceCollection();
        services.AddPlatformObservability(configuration, "Ago.Chat.Worker");
        using var provider = services.BuildServiceProvider();

        var attributes = provider.GetRequiredService<TracerProvider>().GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal("0.0.0", attributes["service.version"]);
        Assert.Equal("local", attributes["deployment.environment"]);
    }

    [Fact]
    public void AddPlatformObservability_MissingExporterEndpoint_FailsFastRatherThanSilentlyExportingNowhere()
    {
        // naming-and-structure.md: "a typo in a key must fail the pod" - Otel:Exporter:Endpoint is
        // [Required] on OtelExporterOptions, validated eagerly (not only deferred to
        // ValidateOnStart's own host-startup check) so a bad value fails the exact call that would
        // otherwise silently start exporting to nothing.
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        var services = new ServiceCollection();
        Assert.Throws<ValidationException>(() => services.AddPlatformObservability(configuration, "Ago.Chat.Api"));
    }

    [Fact]
    public void AddPlatformObservability_RegistersTheSameOptionsThrough_ValidateOnStart()
    {
        // Belt-and-suspenders with the eager check above: the standard AddOptions<T>().Bind().
        // ValidateDataAnnotations().ValidateOnStart() pipeline every other options class in this
        // codebase uses is registered too, so a test (or a real host) exercising IOptions<T>.Value
        // directly still gets the same validation - not a second, divergent mechanism.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Otel:Exporter:Endpoint"] = "http://localhost:4317",
        });

        var services = new ServiceCollection();
        services.AddPlatformObservability(configuration, "Ago.Chat.Api");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlatformObservabilityOptions>>().Value;
        Assert.Equal(new Uri("http://localhost:4317"), options.Exporter.Endpoint);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
