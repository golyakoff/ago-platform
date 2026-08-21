using Ago.Platform.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Platform.Persistence.Postgres;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the generic outbox/inbox implementation against a product's own
    /// <typeparamref name="TContext"/> - the one place a concrete DbContext type meets this
    /// platform project's generic code (adr/0017). Called from a product's own DI wiring
    /// (<c>Ago.Chat.Module</c>), never from here.</summary>
    public static IServiceCollection AddOutboxInbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddScoped<IOutboxWriter, EfOutboxWriter<TContext>>();
        services.AddScoped<IInboxChecker, EfInboxChecker<TContext>>();
        return services;
    }
}
