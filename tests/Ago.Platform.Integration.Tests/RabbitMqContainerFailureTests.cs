using Ago.Platform.Abstractions;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.Options;

namespace Ago.Platform.Integration.Tests;

/// <summary>resilience.md: "outbox accumulates while it is down" depends on a publish against a dead
/// broker failing fast and cleanly, not hanging - a hang here would stall the whole dispatcher loop
/// (2-04) instead of just leaving the row unpublished for the next cycle. Uses its own, non-shared
/// container - stopping the fixture's shared one would break every other test in this collection.</summary>
public sealed class RabbitMqContainerFailureTests
{
    [Fact]
    public async Task PublishAgainstAStoppedBroker_FailsFastRatherThanHanging()
    {
        var container = new Testcontainers.RabbitMq.RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername("ago-test").WithPassword("ago-test-local-dev").Build();
        await container.StartAsync();

        var options = Options.Create(new RabbitMqOptions
        {
            HostName = container.Hostname,
            Port = container.GetMappedPublicPort(5672),
            UserName = "ago-test",
            Password = "ago-test-local-dev",
        });

        await using var connection = new RabbitMqConnection(options);
        var publisher = new RabbitMqEventPublisher(connection);

        // Establishes the connection/channel while the broker is still up, matching a long-lived
        // publisher that only later finds the broker gone - the interesting failure mode.
        var warmUp = new EventEnvelope(Guid.NewGuid(), "warmup-topic", 1, "key", DateTimeOffset.UtcNow, Guid.NewGuid(), "{}");
        await publisher.PublishAsync(warmUp, CancellationToken.None);

        await container.StopAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var envelope = new EventEnvelope(Guid.NewGuid(), "warmup-topic", 1, "key", DateTimeOffset.UtcNow, Guid.NewGuid(), "{}");

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(envelope, cts.Token));
        Assert.False(cts.IsCancellationRequested, "The publish should fail on its own, not because the test's timeout fired.");

        await container.DisposeAsync();
    }
}
