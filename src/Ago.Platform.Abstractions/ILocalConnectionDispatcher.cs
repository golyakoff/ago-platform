namespace Ago.Platform.Abstractions;

/// <summary>
/// The last hop of the fan-out path: push to one connection this process actually owns. Declared
/// here so the generic consumer that receives a <see cref="NodeDelivery"/>
/// (<c>Ago.Platform.Realtime</c>) never needs to know how a connection is actually reached - SignalR,
/// a raw WebSocket, anything - only the host that owns the transport (`Ago.Chat.Api`) implements
/// this. A connection that is no longer locally known (already disconnected) is not an error: the
/// implementation should simply do nothing for it, matching realtime.md's "a stale entry causes a
/// harmless failed delivery."
/// </summary>
public interface ILocalConnectionDispatcher
{
    Task DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken);
}
