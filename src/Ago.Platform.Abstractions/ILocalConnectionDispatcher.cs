namespace Ago.Platform.Abstractions;

/// <summary>
/// The last hop of the fan-out path: push to one connection this process actually owns. Declared
/// here so the generic consumer that receives a <see cref="NodeDelivery"/>
/// (<c>Ago.Platform.Realtime</c>) never needs to know how a connection is actually reached - SignalR,
/// a raw WebSocket, anything - only the host that owns the transport (`Ago.Chat.Api`) implements
/// this. A connection that is no longer locally known (already disconnected) is not an error: the
/// implementation should simply do nothing for it, matching realtime.md's "a stale entry causes a
/// harmless failed delivery."
///
/// `7-08`: doing nothing is still reported, as <see cref="DispatchOutcome.ConnectionNotLocal"/>.
/// The behaviour is unchanged - the caller still treats both outcomes identically and still
/// acknowledges the delivery either way - but the difference is now visible, which it was not, and
/// which is why "did the server even try to deliver to that connection" took an hour of reading
/// code to answer.
/// </summary>
public interface ILocalConnectionDispatcher
{
    /// <summary>Pushes to one connection this process may own. Returns
    /// <see cref="DispatchOutcome.Delivered"/> only if this process actually held the connection and
    /// handed the payload to its transport; <see cref="DispatchOutcome.ConnectionNotLocal"/>
    /// otherwise. Never throws for an unknown connection - that is a no-op, not an error.</summary>
    Task<DispatchOutcome> DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken);
}
