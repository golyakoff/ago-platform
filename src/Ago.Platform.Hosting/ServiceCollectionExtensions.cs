using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Platform.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <c>Kernel</c>'s primitives with their default, real implementations -
    /// <see cref="SystemClock"/> for <see cref="IClock"/>, <see cref="UuidV7Generator"/> for
    /// <see cref="IIdGenerator"/>. Both are stateless and thread-safe, hence singleton.
    /// </summary>
    public static IServiceCollection AddPlatformKernel(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        return services;
    }
}
