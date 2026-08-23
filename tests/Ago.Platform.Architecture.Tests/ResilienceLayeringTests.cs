using NetArchTest.Rules;

namespace Ago.Platform.Architecture.Tests;

/// <summary>
/// <c>6-01</c>'s Done-when: <c>Ago.Platform.Resilience</c> is the only place a Polly
/// <c>ResiliencePipelineBuilder</c> may be constructed. Before this item, both
/// <c>Ago.Platform.Caching.Redis</c> and <c>Ago.Platform.Storage.S3</c> built their own privately,
/// with no shared code between them; this asserts the dedup is real and stays real - a future
/// infrastructure project reaching for <c>new ResiliencePipelineBuilder()</c> instead of
/// <c>ResiliencePolicyBuilder</c> fails here instead of quietly reintroducing a fourth ad hoc
/// pipeline (<c>6-05</c>'s webhook dispatcher is exactly the next project this guards).
/// </summary>
public class ResilienceLayeringTests
{
    [Fact]
    public void OnlyResilience_EverConstructsAResiliencePipelineBuilder()
    {
        AssertNoResiliencePipelineBuilderDependency(
            typeof(Ago.Platform.Caching.Redis.ServiceCollectionExtensions).Assembly, "Ago.Platform.Caching.Redis");
        AssertNoResiliencePipelineBuilderDependency(
            typeof(Ago.Platform.Storage.S3.ServiceCollectionExtensions).Assembly, "Ago.Platform.Storage.S3");
        AssertNoResiliencePipelineBuilderDependency(
            typeof(Ago.Platform.Persistence.Postgres.OutboxMessage).Assembly, "Ago.Platform.Persistence.Postgres");
        AssertNoResiliencePipelineBuilderDependency(
            typeof(Ago.Platform.Messaging.RabbitMq.RabbitMqEventPublisher).Assembly, "Ago.Platform.Messaging.RabbitMq");
        AssertNoResiliencePipelineBuilderDependency(
            typeof(Ago.Platform.Realtime.RedisConnectionRegistry).Assembly, "Ago.Platform.Realtime");

        // Ago.Platform.Resilience itself is deliberately not checked here - it is the one place the
        // type is allowed to appear.
    }

    private static void AssertNoResiliencePipelineBuilderDependency(System.Reflection.Assembly assembly, string assemblyName)
    {
        var result = Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOn("Polly.ResiliencePipelineBuilder")
            .GetResult();

        var offenders = result.FailingTypeNames is null ? "n/a" : string.Join(", ", result.FailingTypeNames);
        Assert.True(result.IsSuccessful,
            $"{assemblyName} must not construct Polly.ResiliencePipelineBuilder directly - use Ago.Platform.Resilience.ResiliencePolicyBuilder instead. Offenders: {offenders}");
    }
}
