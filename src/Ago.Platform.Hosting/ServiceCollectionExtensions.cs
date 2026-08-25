using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Platform.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <c>Kernel</c>'s primitives with their default, real implementations -
    /// <see cref="SystemClock"/> for <see cref="IClock"/>, <see cref="UuidV7Generator"/> for
    /// <see cref="IIdGenerator"/>. Both are stateless and thread-safe, hence singleton.
    ///
    /// `7-09`: this is the other half of what makes this package's dependency list load-bearing.
    /// Every host, of every shape, calls this - a <c>Microsoft.NET.Sdk.Worker</c> generic host whose
    /// <c>Program.cs</c> contains nothing but this call is a real and expected shape (that is exactly
    /// what <c>Ago.Calendar.Worker</c> was when it found the defect adr/0046 records). So the cost of
    /// a dependency added to this project is paid by hosts that will never call anything but this
    /// method.
    /// </summary>
    public static IServiceCollection AddPlatformKernel(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        return services;
    }
}
