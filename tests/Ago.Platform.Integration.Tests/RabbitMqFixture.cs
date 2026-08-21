using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.Options;
using Testcontainers.RabbitMq;

namespace Ago.Platform.Integration.Tests;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; private set; } = null!;

    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";

    public async Task InitializeAsync()
    {
        // Explicit credentials, not the image's own "guest" default - RabbitMQ restricts "guest" to
        // loopback connections by policy, and the container's port-forwarded address does not count
        // as loopback from the broker's own point of view.
        Container = new RabbitMqBuilder("rabbitmq:4-management").WithUsername(Username).WithPassword(Password).Build();
        await Container.StartAsync();
    }

    public async Task DisposeAsync() => await Container.DisposeAsync();

    public IOptions<RabbitMqOptions> CreateOptions() => Options.Create(new RabbitMqOptions
    {
        HostName = Container.Hostname,
        Port = Container.GetMappedPublicPort(5672),
        UserName = Username,
        Password = Password,
    });
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "RabbitMq";
}
