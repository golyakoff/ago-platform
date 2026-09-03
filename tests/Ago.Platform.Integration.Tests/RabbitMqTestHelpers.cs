using Ago.Platform.Messaging.RabbitMq;

namespace Ago.Platform.Integration.Tests;

internal static class RabbitMqTestHelpers
{
    public static string NewTopic() => $"test.{Guid.NewGuid():N}";

    /// <summary>testing.md: "poll a condition with a timeout" instead of Thread.Sleep - delivery
    /// happens on a background consumer callback, so there is no single awaitable for "done".</summary>
    public static Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return await condition();
    }

    /// <summary>
    /// `15-15`'s own existence check: a passive declare succeeds silently when the queue is there and
    /// closes the channel it was attempted on when it is not - the standard AMQP way to ask "does this
    /// exist" without side effects on a queue that does. Always opens a *fresh* channel, because a
    /// failed passive declare leaves the channel it ran on closed and unusable for anything else,
    /// including a second check.
    ///
    /// Two different failure codes both close the channel, and only one of them means "gone": 404
    /// (NOT_FOUND) does, but 405 (RESOURCE_LOCKED - "cannot obtain exclusive access to locked queue")
    /// means the queue is exclusive to a *different* connection and very much still exists, which is
    /// exactly the state this checker (deliberately its own connection, separate from whichever one
    /// declared an exclusive/ProcessScoped queue) finds it in for as long as the owner is alive.
    /// Collapsing both codes into "false" would make a ProcessScoped queue look gone before its
    /// declaring connection ever closed - the opposite of what this helper exists to prove.
    /// </summary>
    public static async Task<bool> QueueExistsAsync(RabbitMqConnection connection, string queueName)
    {
        var channel = await connection.CreateChannelAsync();
        try
        {
            await channel.QueueDeclarePassiveAsync(queueName);
            return true;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            return false;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 405)
        {
            return true;
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }
}
