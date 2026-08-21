using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Ago.Platform.Integration.Tests;

/// <summary>resilience.md's Redis row: "fallback to cache miss - never surface an error" -
/// realtime.md's own failure table says the same for the connection registry specifically ("New
/// connections still accepted"). A registry call against a dead Redis must degrade to a no-op, never
/// throw into a hub method. Uses its own, non-shared container so stopping it cannot affect any other
/// test in the shared collection (matching RabbitMqContainerFailureTests' own reasoning).</summary>
public sealed class ConnectionRegistryContainerFailureTests
{
    [Fact]
    public async Task RegisterAgainstAStoppedRedis_DoesNotThrow()
    {
        var container = new RedisBuilder("redis:7-alpine").Build();
        await container.StartAsync();

        var configuration = ConfigurationOptions.Parse(container.GetConnectionString());
        configuration.ConnectTimeout = 2000;
        configuration.SyncTimeout = 2000;
        configuration.AbortOnConnectFail = false; // keep retrying in the background rather than throwing on connect

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
        var registry = new RedisConnectionRegistry(
            multiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);

        // Warm up while Redis is still up, matching the RabbitMq container-failure test's own
        // reasoning: a long-lived registry that only later finds Redis gone is the interesting case.
        var principal = new PrincipalKey($"visitor:{Guid.NewGuid()}");
        await registry.RegisterAsync(new ConnectionId("warmup"), new NodeId("node-a"), principal, CancellationToken.None);

        await container.StopAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exception = await Record.ExceptionAsync(() =>
            registry.RegisterAsync(new ConnectionId(Guid.NewGuid().ToString()), new NodeId("node-a"), principal, cts.Token));

        Assert.Null(exception);
        Assert.False(cts.IsCancellationRequested, "Should have returned on its own, not because the test's timeout fired.");

        await container.DisposeAsync();
    }
}
