namespace Ago.Platform.Abstractions;

/// <summary>
/// The publish half of realtime.md's Fan-out path: resolve a set of principals' connections via
/// <see cref="IConnectionRegistry"/>, group by node, publish one <see cref="NodeDelivery"/> per node
/// through the broker. A product's Application layer calls this - never <see cref="IEventPublisher"/>
/// directly for this purpose - so the resolve-group-publish mechanics stay in one place
/// (`Ago.Platform.Realtime`) rather than duplicated per caller.
///
/// `7-08`: returns what it resolved (<see cref="FanoutResult"/>) so the product can dimension it in
/// its own vocabulary. See that type for why the platform reports the numbers instead of recording
/// them itself.
/// </summary>
public interface INodeFanoutPublisher
{
    Task<FanoutResult> PublishAsync(
        IReadOnlyCollection<PrincipalKey> recipients,
        string method,
        string payloadJson,
        Guid correlationId,
        CancellationToken cancellationToken);
}
