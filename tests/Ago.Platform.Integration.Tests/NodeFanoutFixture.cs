using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Ago.Platform.Integration.Tests;

/// <summary>3-02's fan-out path genuinely spans two external resources (the registry on Redis, the
/// broker on RabbitMQ), so this combines both container lifecycles in one fixture rather than
/// forcing a test class to carry two `[Collection]` attributes (xUnit allows only one).</summary>
public sealed class NodeFanoutFixture : IAsyncLifetime
{
    private const string RabbitMqUsername = "ago-test";
    private const string RabbitMqPassword = "ago-test-local-dev";

    private RedisContainer _redis = null!;
    private RabbitMqContainer _rabbitMq = null!;

    public IConnectionMultiplexer RedisMultiplexer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _redis = new RedisBuilder("redis:7-alpine").Build();
        _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(RabbitMqUsername).WithPassword(RabbitMqPassword).Build();

        await Task.WhenAll(_redis.StartAsync(), _rabbitMq.StartAsync());
        RedisMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await RedisMultiplexer.DisposeAsync();
        await Task.WhenAll(_redis.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    public IOptions<RabbitMqOptions> CreateRabbitMqOptions() => Options.Create(new RabbitMqOptions
    {
        HostName = _rabbitMq.Hostname,
        Port = _rabbitMq.GetMappedPublicPort(5672),
        UserName = RabbitMqUsername,
        Password = RabbitMqPassword,
    });
}

[CollectionDefinition(Name)]
public sealed class NodeFanoutCollection : ICollectionFixture<NodeFanoutFixture>
{
    public const string Name = "NodeFanout";
}
