using Microsoft.AspNetCore.Http;

namespace Ago.Platform.Observability;

/// <summary>
/// `26-331`: which inbound requests do not get a trace. Kept as its own type, not a lambda inside
/// <c>AddPlatformObservability</c>, so the rule is a function a unit test can call without standing up
/// a server.
///
/// The two prefixes are not configurable on purpose: <c>/metrics</c> is the path
/// <c>app.MapPrometheusScrapingEndpoint()</c> maps - this package's own endpoint - and <c>/healthz</c>
/// is the probe prefix every host's manifests already use (`edge.md`). Matching is by path *segment*
/// (<see cref="PathString.StartsWithSegments(PathString)"/>), so <c>/healthz/ready</c> is skipped and
/// <c>/healthzone</c> or <c>/metrics-export</c> are not.
/// </summary>
public static class TracePathFilter
{
    private static readonly PathString[] IgnoredPrefixes = ["/healthz", "/metrics"];

    /// <summary>False for a health probe or a metrics scrape; true for everything else.</summary>
    public static bool ShouldTrace(HttpContext context) => ShouldTrace(context.Request.Path);

    /// <inheritdoc cref="ShouldTrace(HttpContext)"/>
    public static bool ShouldTrace(PathString path)
    {
        foreach (var prefix in IgnoredPrefixes)
        {
            if (path.StartsWithSegments(prefix))
            {
                return false;
            }
        }

        return true;
    }
}
