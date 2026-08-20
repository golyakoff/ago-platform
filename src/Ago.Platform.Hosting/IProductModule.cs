using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Platform.Hosting;

/// <summary>
/// The platform/product seam: a host (<c>Ago.Chat.Api</c>, <c>Ago.Chat.Worker</c>,
/// <c>Ago.Chat.Webhooks</c>) is a thin composition root that loads one <see cref="IProductModule"/>
/// and nothing product-specific of its own (docs/architecture/clean-architecture.md). Endpoint, hub
/// and consumer registration hooks are deliberately not here yet - Stage 0 has no module to prove
/// them against, and a shape guessed from zero callers is a shape that will need to change.
/// </summary>
public interface IProductModule
{
    string Name { get; }

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
