namespace Ago.Platform.Abstractions;

/// <summary>
/// The publish half of realtime.md's Fan-out path: resolve a set of principals' connections via
/// <see cref="IConnectionRegistry"/>, group by node, publish one <see cref="NodeDelivery"/> per node
/// through the broker. A product's Application layer calls this - never <see cref="IEventPublisher"/>
/// directly for this purpose - so the resolve-group-publish mechanics stay in one place
/// (`Ago.Platform.Realtime`) rather than duplicated per caller.
/// </summary>
public interface INodeFanoutPublisher
{
    Task PublishAsync(
        IReadOnlyCollection<PrincipalKey> recipients,
        string method,
        string payloadJson,
        Guid correlationId,
        CancellationToken cancellationToken);
}
