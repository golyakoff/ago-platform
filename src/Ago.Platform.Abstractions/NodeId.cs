namespace Ago.Platform.Abstractions;

/// <summary>
/// One process's stable identity for its own lifetime (realtime.md: "who is connected where").
/// In Kubernetes this is the pod name (the <c>HOSTNAME</c> env var); outside it, whatever the host
/// chooses, as long as it is stable across the process's own lifetime - the registry never needs it
/// to mean anything beyond "the same value the last time this process registered a connection."
/// </summary>
public readonly record struct NodeId(string Value)
{
    public override string ToString() => Value;
}
