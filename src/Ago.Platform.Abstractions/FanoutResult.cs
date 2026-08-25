namespace Ago.Platform.Abstractions;

/// <summary>
/// One principal a fan-out was asked to reach, and how many live connections
/// <see cref="IConnectionRegistry"/> had for them at that moment. <see cref="Connections"/> is
/// advice, not truth (`adr/0009`): zero means the registry knew of nobody, not that nobody exists.
/// </summary>
public readonly record struct ResolvedRecipient(PrincipalKey Recipient, int Connections);

/// <summary>
/// What <see cref="INodeFanoutPublisher.PublishAsync"/> found while resolving a fan-out.
///
/// `7-08`: returned rather than turned into a metric here, because the dimension that makes the
/// number useful is *which kind of principal* this was - a visitor with no connection is ordinary,
/// an operator with none is not - and "visitor" and "operator" are chat concepts the platform must
/// never learn (clean-architecture.md's qualifying rule). Deriving a metric tag from
/// <see cref="PrincipalKey"/>'s own text would also make the platform own an instrument whose
/// cardinality it cannot bound: a product that did not namespace its keys would get one time series
/// per visitor. So the platform reports facts and the product names them.
/// </summary>
public sealed record FanoutResult(IReadOnlyList<ResolvedRecipient> Recipients)
{
    /// <summary>A fan-out that resolved nothing - for a caller (or a test double) that has no
    /// recipients to report.</summary>
    public static FanoutResult Empty { get; } = new([]);

    /// <summary>Connections across every recipient - what "reached how many live connections"
    /// means at this hop, as distinct from how many recipients were resolved. One recipient with
    /// three open tabs is three.</summary>
    public int TotalConnections
    {
        get
        {
            var total = 0;
            foreach (var recipient in Recipients)
            {
                total += recipient.Connections;
            }

            return total;
        }
    }
}
