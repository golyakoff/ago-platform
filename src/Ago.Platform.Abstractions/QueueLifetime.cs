namespace Ago.Platform.Abstractions;

/// <summary>
/// How long a <see cref="SubscriptionMode.Competing"/> subscription's queue outlives its consumer.
/// Orthogonal to <see cref="SubscriptionMode"/>: mode is about *how a message is routed* (load-shared
/// across replicas vs. delivered to every node), lifetime is about *how long the queue that routing
/// depends on sticks around*. Before `15-15` these two axes were collapsed into one hardcoded
/// `durable: true, autoDelete: false` for every `Competing` subscription, which is correct for a
/// durable subscription and wrong for a queue whose consumer name already names something ephemeral -
/// see <see cref="ProcessScoped"/>.
///
/// <see cref="SubscriptionMode.Broadcast"/> never takes this parameter: its own queue is already
/// exclusive+auto-delete by construction (a fresh, randomly-named queue every subscribe call), which
/// is exactly what <see cref="ProcessScoped"/> means - Broadcast just never had a name stable enough
/// to make "durable" a meaningful choice in the first place.
/// </summary>
public enum QueueLifetime
{
    /// <summary>
    /// The default, and the only shape `Competing` had before `15-15`. The queue survives every
    /// consumer disconnecting - a message published while nobody is attached is still there when a
    /// consumer (re)attaches. This is what lets at-least-once delivery survive a rolling deploy: every
    /// genuinely durable subscription (`OperatorRemovedFromSite.operator-removed`,
    /// `ConversationAssignedToOperator.conversation-assignment-fanout`, and the rest of
    /// `messaging.md`'s topic table) must keep using this, because auto-deleting their queues would
    /// silently drop messages published while no replica happened to be attached - the symptom would
    /// be lost work, not an error.
    /// </summary>
    Durable,

    /// <summary>
    /// The queue exists only for as long as the connection that declared it is open - gone the moment
    /// that connection closes, whether the process exited cleanly or was killed. Correct only when the
    /// consumer name already identifies something with no life of its own beyond one process: a
    /// specific pod, a specific connection. `NodeDeliveryConsumer`'s node-delivery queue
    /// (`deliver-to-connections.&lt;pod&gt;`) is the motivating case (`15-15`) - once a pod is gone,
    /// nothing will ever read that pod's own topic again, so a durable queue there only accumulates
    /// dead state (measured: 71 of 72 such queues on the live broker belonged to pods that no longer
    /// existed). Choosing this for a subscription that other replicas of the same logical consumer are
    /// expected to reattach to independently of this one process would silently drop whatever was
    /// published in the gap - the same failure mode <see cref="Durable"/>'s own remarks describe, so
    /// this is deliberately not something a consumer name is ever inferred into; the caller states it.
    /// </summary>
    ProcessScoped,
}
