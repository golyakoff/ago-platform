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
}
