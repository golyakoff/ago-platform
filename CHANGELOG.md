# Changelog

All notable changes to `Ago.Platform.*` are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning is
[SemVer](https://semver.org/) (`docs/architecture/repositories.md`).

## [0.9.0] - 2026-08-22

### Fixed

- `Ago.Platform.Abstractions.ICache`/`Ago.Platform.Caching.Redis.RedisCache`: `GetAsync<T>`,
  `SetAsync<T>` and `GetOrCreateAsync<T>` now constrain `where T : class`. Found live while building
  `ago-chat`'s `5-01`: for an unconstrained generic parameter, C#'s `T?` return annotation has no
  runtime effect when `T` is instantiated with a value type - `default(T?)` for `T = bool` is
  `false`, not a distinguishable null - so `GetOrCreateAsync`'s own `is { }`/`is null` checks could
  not tell a cold key apart from a genuinely-cached `false`/`0`, and silently never called the
  factory at all for a legitimately falsy/zero result. Every existing caller happened to avoid this
  by only ever caching reference-type DTOs (`GetSiteConfigByPublicKeyHandler`'s `SiteLookupResult`);
  the constraint turns "silently wrong for a value type" into a compile error instead of leaving the
  port able to misbehave the same way again.

### Breaking

- Any caller passing a value type directly to `ICache.GetAsync<T>`/`SetAsync<T>`/
  `GetOrCreateAsync<T>` no longer compiles - wrap it in a small reference-type record instead
  (the pattern every real caller already used).

## [0.8.0] - 2026-08-22

### Added

- `Ago.Platform.Caching.Redis`: `RedisDistributedLock` - a public, fail-closed distributed lock
  (`SET NX` acquire, token-checked Lua-script release), for `ago-chat`'s `4-03` assignment-engine
  alternative (per-operator mutual exclusion via Redis instead of `SKIP LOCKED`). Deliberately not
  a reuse of the existing internal `RedisLock` (`3-04`'s cache-stampede helper): that one fails open
  on an unreachable Redis, correct for a redundant cache load but wrong here, where failing open
  would mean every caller proceeding as if it held an exclusive lock it never actually got.

## [0.7.0] - 2026-08-22

### Added

- `Ago.Platform.Realtime`: `DrainState`, `DrainOptions`, `ConnectionDrainCoordinator` -
  `concurrency.md`'s graceful-shutdown sequence, the connection-holding half (3-06). Registers
  against `IHostApplicationLifetime.ApplicationStopping` so `DrainState` flips synchronously the
  instant shutdown begins; the real work (push a jittered `Reconnect` to every locally-tracked
  connection via `ILocalConnectionDispatcher`, remove the node's registry entries, wait for
  connections to actually drop or a bounded timeout) runs from an overridden `StopAsync`.

## [0.6.0] - 2026-08-22

### Added

- `Ago.Platform.Abstractions`: `IRateLimiter`, `RateLimitKey`, `RateLimitRule`, `RateLimitDecision` -
  `caching.md`'s token-bucket rate-limiting port (3-05).
- `Ago.Platform.Caching.Redis`: `RedisRateLimiter` - a Lua script doing the atomic
  check-and-decrement in one round trip (real Redis `TIME`, not the caller's clock, so two nodes
  racing the same bucket agree on elapsed time), sharing `RedisCache`'s `IConnectionMultiplexer` and
  `ResiliencePipeline`. Fails open (`Allowed: true`) on any Redis failure, per `adr/0009`.

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
