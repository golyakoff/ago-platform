# Changelog

All notable changes to `Ago.Platform.*` are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning is
[SemVer](https://semver.org/) (`docs/architecture/repositories.md`).

## [0.5.0] - 2026-08-22

### Added

- `Ago.Platform.Abstractions`: `ICache`, `CacheKey`, `CacheEntryOptions` - `caching.md`'s cache-aside
  port (3-04).
- `Ago.Platform.Caching.Redis` (new project): `RedisCache` (the port implemented against Redis - real
  stampede protection via in-process single-flight plus a short cross-node `RedisLock`, TTL jitter,
  a shared Polly circuit breaker + timeout degrading every failure to a cache miss rather than an
  exception), `CacheInvalidationPublisher`/`CacheInvalidationConsumer` (the generic
  `SubscriptionMode.Broadcast` invalidation broadcast `messaging.md`'s Topics table names).

## [0.4.0] - 2026-08-22

### Added

- `Ago.Platform.Abstractions`: `NodeDelivery`, `ILocalConnectionDispatcher`, `INodeFanoutPublisher` -
  the targeted cross-node delivery contract realtime.md's Fan-out path describes (3-02).
- `Ago.Platform.Realtime`: `NodeTopics`, `NodeFanoutPublisher` (resolves recipients via
  `IConnectionRegistry`, groups by node, publishes one `NodeDelivery` per node), `NodeDeliveryConsumer`
  (a node's own consumer of its topic, dispatching each connection via
  `ILocalConnectionDispatcher`).

### Changed

- `IEventPublisher.PublishAsync`'s doc comment now names two legitimate caller categories (the
  outbox dispatcher, and ephemeral fan-out derived from an already-outboxed event - `adr/0020`)
  instead of claiming a single caller that is no longer true. No signature change.

## [0.3.0] - 2026-08-22

### Added

- `Ago.Platform.Realtime` (new project): `IConnectionRegistry` (declared in
  `Ago.Platform.Abstractions`, alongside `ConnectionId`, `NodeId`, `PrincipalKey`,
  `RegisteredConnection`) - the Redis-backed "who is connected where" registry `realtime.md`
  describes, plus `ConnectionHeartbeat` and `LocalConnectionTracker` for keeping registered
  connections' TTLs alive. Depends on `StackExchange.Redis`.

## [0.2.2] - 2026-08-21

### Fixed

- `Ago.Platform.Messaging.RabbitMq`: `RabbitMqEventPublisher` now discards its cached channel after
  any failed publish instead of trusting `IChannel.IsOpen`, which was observed staying `true` for
  60s+ after a real broker outage had genuinely ended. The next publish attempt now always
  negotiates a fresh channel rather than risking one left in an ambiguous state.

## [0.2.1] - 2026-08-21

### Fixed

- `Ago.Platform.Persistence.Postgres`: `EfOutboxWriter` was not persisting
  `EventEnvelope.Version`/`CorrelationId` onto outbox rows, so nothing reading a claimed row back
  out could reconstruct a full envelope. Found while building `ago-chat`'s outbox dispatcher.
- `Ago.Platform.Messaging.RabbitMq`: `RabbitMqConnection`'s heartbeat used the client's 60s default,
  so a silently-dead connection (broker paused or network-partitioned, no TCP FIN/RST) could take
  minutes to be noticed before automatic recovery even started. Shortened to 10s.

## [0.2.0] - 2026-08-21

### Added

- `Ago.Platform.Hosting`: `SystemClock` (the real `IClock`), `UuidV7Generator` registration, and
  `AddPlatformKernel()` to wire both into a host's `IServiceCollection` in one call.

## [0.1.0] - 2026-08-20

### Added

- `Ago.Platform.Kernel`: `Result`, `Result<T>`, `Error`, `IStronglyTypedId`, `IClock`,
  `IIdGenerator` and its default `UuidV7Generator`.
- `Ago.Platform.Hosting`: `IProductModule`, the platform/product seam a host loads.
