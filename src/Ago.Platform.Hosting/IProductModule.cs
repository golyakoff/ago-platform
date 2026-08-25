using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Platform.Hosting;

/// <summary>
/// The platform/product seam: a host is a thin composition root that loads one
/// <see cref="IProductModule"/> and nothing product-specific of its own
/// (docs/architecture/clean-architecture.md). Endpoint, hub and consumer registration hooks are
/// deliberately not here yet - Stage 0 had no module to prove them against, and a shape guessed from
/// zero callers is a shape that will need to change.
///
/// **This interface names no host and no product deliberately** (`7-09`). It used to list
/// <c>Ago.Chat.Api</c>/<c>Worker</c>/<c>Webhooks</c> as though those were the hosts, which was true
/// while there was one product and stopped being true the moment there were two. The hosts that load
/// a module are whatever each product ships, of whatever SDK shape - a
/// <c>Microsoft.NET.Sdk.Web</c> API host and a <c>Microsoft.NET.Sdk.Worker</c> generic host are both
/// ordinary cases, and this package must cost the second one no more than the first (adr/0046).
/// </summary>
public interface IProductModule
{
    string Name { get; }

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
