using System.Reflection;
using Ago.Platform.Abstractions;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Platform.Integration.Tests;

/// <summary>
/// 17-09: <c>RabbitMqConnection.DisposeAsync</c> used to let a <see cref="TaskCanceledException"/>
/// escape when the broker never answered the client's own close handshake, and never reached
/// <c>_lock.Dispose()</c> on that path.
///
/// Reproducing the escape turned out to need more than "broker gone, then dispose" - a plain
/// paused broker with an otherwise idle connection was tried first and the underlying client's own
/// <c>DisposeAsync</c> absorbed it silently every time (confirmed empirically, not assumed: several
/// idle-connection and idle-channel variants against a paused container all completed in 20-25s with
/// no exception). What actually reproduces it, reliably, is the ticket's own named scenario -
/// <c>UnreadCounterShutdownTests.KillingTheConsumerMidBatch...</c> - literally: a competing consumer
/// with several deliveries genuinely in flight (blocked inside its handler, not idle) when the broker
/// stops answering. <c>Channel.DisposeAsync</c> then has to reconcile that in-flight dispatch work
/// while the AMQP close handshake is also going nowhere, and *that* combination is what lets the
/// <see cref="TaskCanceledException"/> through - confirmed by running the harness below against the
/// unfixed method body before writing the fix (see the commit-prep notes for this item).
///
/// Own standalone container per test, not the fixture's shared one - pausing it would freeze every
/// other test sharing the collection (<c>RabbitMqContainerFailureTests</c>'s own reasoning for the
/// same choice).
/// </summary>
public sealed class RabbitMqConnectionDisposeTests
{
    private static readonly RetryPolicy RetryPolicy = new(MaxAttempts: 3, InitialBackoff: TimeSpan.FromMilliseconds(200), DeadLetterName: $"dlq.{Guid.NewGuid():N}");

    [Fact]
    public async Task DisposeAsync_WhileAConsumerIsMidBatchAndTheBrokerGoesUnresponsive_CompletesWithoutThrowing()
    {
        var (container, connection) = await BuildPausedMidBatchScenarioAsync();
        try
        {
            // The client's own close handshake involves several internally-bounded waits that can
            // stack up (confirmed empirically at 20-40s in this scenario); this timeout only guards
            // against the test itself hanging if that internal bound is ever wrong - not what proves
            // the behaviour under test.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var disposeTask = connection.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(disposeTask, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
            Assert.Same(disposeTask, completed);

            // The assertion that matters: awaiting a faulted task rethrows, so this line is where the
            // pre-fix code fails with TaskCanceledException.
            await disposeTask;
        }
        finally
        {
            await container.UnpauseAsync();
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_WhileAConsumerIsMidBatchAndTheBrokerGoesUnresponsive_StillDisposesTheLock()
    {
        var (container, connection) = await BuildPausedMidBatchScenarioAsync();
        try
        {
            // This test cares only about the lock's fate, not about whether DisposeAsync itself
            // throws - that is the previous test's own, single behaviour (testing.md: one behaviour
            // per test). Swallow whatever DisposeAsync does here so a regression in the "does not
            // throw" guarantee cannot also fail this assertion for an unrelated reason.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var disposeTask = connection.DisposeAsync().AsTask();
                await Task.WhenAny(disposeTask, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
                await disposeTask;
            }
            catch
            {
            }

            // SemaphoreSlim exposes no public "am I disposed" property - a WaitAsync() call throwing
            // ObjectDisposedException is the only observable proof that Dispose() actually reached
            // this instance, which is exactly the fact "the lock leaks when [DisposeAsync] throws" is
            // about: _lock.Dispose() sitting after the throwing await, never reached. Reflection onto
            // the private field is the only way to ask that question without adding a test-only seam
            // to a NuGet-shipped public type's surface.
            var lockField = typeof(RabbitMqConnection).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("RabbitMqConnection no longer has a private field named _lock.");
            var @lock = (SemaphoreSlim)lockField.GetValue(connection)!;

            await Assert.ThrowsAsync<ObjectDisposedException>(() => @lock.WaitAsync());
        }
        finally
        {
            await container.UnpauseAsync();
            await container.DisposeAsync();
        }
    }

    /// <summary>Starts a broker, subscribes a competing consumer whose handler blocks once it
    /// receives a delivery (so there are messages genuinely in flight, not an idle connection),
    /// publishes enough messages to fill that consumer's prefetch, waits until the handler is
    /// actually inside its blocking wait, then pauses the broker - leaving the returned connection
    /// ready to be disposed against a broker that is up but will never answer again.</summary>
    private static async Task<(Testcontainers.RabbitMq.RabbitMqContainer Container, RabbitMqConnection Connection)> BuildPausedMidBatchScenarioAsync()
    {
        var container = new Testcontainers.RabbitMq.RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername("ago-test").WithPassword("ago-test-local-dev").Build();
        await container.StartAsync();

        var connection = new RabbitMqConnection(OptionsFor(container), NullLogger<RabbitMqConnection>.Instance);
        var publisher = new RabbitMqEventPublisher(connection, NullLogger<RabbitMqEventPublisher>.Instance);
        var consumer = new RabbitMqEventConsumer(connection);

        var topic = RabbitMqTestHelpers.NewTopic();
        var midBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await consumer.SubscribeAsync(topic, SubscriptionMode.Competing, "mid-batch-consumer", RetryPolicy, async (_, ctx, ct) =>
        {
            midBatch.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(8), ct); // "mid-batch": still inside the handler.
            await ctx.AckAsync(ct);
        }, CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            var envelope = new EventEnvelope(Guid.NewGuid(), topic, 1, $"key-{i}", DateTimeOffset.UtcNow, Guid.NewGuid(), "{}");
            await publisher.PublishAsync(envelope, CancellationToken.None);
        }

        await midBatch.Task;

        await container.PauseAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // lets the pause actually take effect before dispose races it.

        return (container, connection);
    }

    private static IOptions<RabbitMqOptions> OptionsFor(Testcontainers.RabbitMq.RabbitMqContainer container) =>
        Options.Create(new RabbitMqOptions
        {
            HostName = container.Hostname,
            Port = container.GetMappedPublicPort(5672),
            UserName = "ago-test",
            Password = "ago-test-local-dev",
        });
}
