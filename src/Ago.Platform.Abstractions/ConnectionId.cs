namespace Ago.Platform.Abstractions;

/// <summary>One realtime transport connection - a SignalR <c>ConnectionId</c> today, whatever the
/// transport is tomorrow. Opaque to the registry; it never parses this, only stores and looks it
/// up.</summary>
public readonly record struct ConnectionId(string Value)
{
    public override string ToString() => Value;
}
