# Changelog

All notable changes to `Ago.Platform.*` are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning is
[SemVer](https://semver.org/) (`docs/architecture/repositories.md`).

## [0.14.0] - 2026-08-24

### Added

- `Ago.Platform.Hosting.AddPlatformObservability` now also wires the OpenTelemetry SDK's `MeterProvider`
  (`7-02`): ASP.NET Core and `HttpClient` metrics instrumentation (the same two packages `7-01` already
  referenced for tracing double as their own metrics source - no new package), a Prometheus scrape
  endpoint (`OpenTelemetry.Exporter.Prometheus.AspNetCore`, pinned `1.18.0-beta.1` - this package has
  never shipped a stable release, tracking the SDK's own 1.18.0 line instead), and a new `"Ago.*"`
  wildcard `Meter` subscription (`ServiceCollectionExtensions.MeterWildcard`) mirroring `7-01`'s
  `ActivitySourceWildcard`. Metrics deliberately do **not** share tracing's OTLP-push exporter to
  `Otel:Exporter:Endpoint` (Jaeger) - an earlier draft of this change did exactly that and shipped with
  it, until live verification while building `7-03` found every metric silently going nowhere: Jaeger's
  OTLP receiver only implements the trace collector service, and Prometheus's own model is pull/scrape
  in the first place, not push. Folded into the existing method rather than a new sibling
  `AddPlatformMetrics` - every host already calls `AddPlatformObservability` exactly once from its own
  `Program.cs`, and both signals configure the same OTel SDK builder, so a second call would only
  duplicate resource/options setup and add a step to forget.
- `Ago.Platform.Resilience.ResiliencePolicyBuilder`'s constructor now takes a required `pipelineName`
  (a breaking change to this pre-1.0 package - every caller already had this value at hand as its own
  private `PipelineName` constant, e.g. `Ago.Platform.Caching.Redis`'s `"Redis"`,
  `Ago.Platform.Storage.S3`'s `"S3"`): `WithCircuitBreaker` now exports a per-pipeline breaker-state
  gauge (`ago.platform.resilience.circuit_breaker.state`, tagged `pipeline`/`state`, one measurement per
  state per collection - the standard "state as a 0/1-valued label" shape) via Polly's own
  `CircuitBreakerStateProvider`, and `WithBulkhead` now exports a bulkhead-rejection counter
  (`ago.platform.resilience.bulkhead.rejections`, tagged `pipeline`) via Polly's `AddRateLimiter(RateLimiterStrategyOptions)`
  overload (switched from the `AddConcurrencyLimiter(int, int)` convenience overload, which has no
  `OnRejected` hook to attach the counter to). Both instruments live in the shared pipeline builder
  itself, not duplicated per boundary - `nfr.md`'s "breaker state... per named resilience pipeline"
  covers `6-05`'s future webhook-dispatcher pipeline automatically once it registers.
- `Ago.Platform.Caching.Redis.CachingMetrics`: a cache-access counter
  (`ago.platform.caching.redis.cache_access`, tagged `namespace` - parsed off `CacheKey`'s own
  documented `{namespace}:{id}` convention - and `outcome`, `hit`/`miss`), recorded once inside
  `RedisCache.GetAsync` (the one method every read path, including `GetOrCreateAsync`'s own
  double-checked reads, funnels through). A Redis failure is treated as a miss for the caller but is
  deliberately *not* counted here, to avoid silently depressing the hit ratio during an outage that is
  already separately observable via the new breaker-state gauge above.
- `Ago.Platform.Realtime.RealtimeMetrics`: a connections-per-node gauge
  (`ago.platform.realtime.connections`, tagged `node`), sourced from `RedisConnectionRegistry`'s own
  local bookkeeping (updated unconditionally in `RegisterAsync`/`UnregisterAsync`/`RemoveNodeAsync`,
  before the Redis write is attempted) rather than a live Redis query at collection time - an
  `ObservableGauge` callback runs synchronously and must not block on network I/O, and a registry read
  failing must never be the reason a metrics scrape fails either (`realtime.md`'s own "advice, not
  truth" contract, extended from staleness/errors to this gauge).
- `Ago.Platform.Messaging.RabbitMq`: a per-consumer RED triad
  (`ago.platform.messaging.rabbitmq.consumer.duration`, `...consumer.count` tagged `topic`/`consumer`/
  `outcome`) and a dead-letter counter (`...dead_lettered`, tagged `event_type`), both added at
  `RabbitMqEventConsumer`'s own generic handler-invocation boundary (the same `"{topic} process"` span
  boundary `7-01` already named) and `RabbitMqMessageContext.DeadLetterAsync`'s own single choke point -
  every product consumer this platform hosts gets RED and DLQ counting for free, with no change to
  `Ago.Chat.Worker`'s own consumer classes, the same "instrument the generic adapter once" placement
  `7-01`'s tracing already established for this exact boundary.

## [0.13.0] - 2026-08-24

### Added

- `Ago.Platform.Hosting.AddPlatformObservability(configuration, serviceName)`: wires the OpenTelemetry
  SDK's tracing provider - ASP.NET Core instrumentation, `HttpClient` instrumentation, Npgsql
  instrumentation (no extra package: Npgsql has emitted its own Activities on an ActivitySource named
  `Npgsql` since 6.0, picked up here with a bare `.AddSource("Npgsql")`), resource attributes
  (`service.name` from the caller-supplied `serviceName`, `service.version`/`deployment.environment`
  from the new `Otel:*`-bound, startup-validated `PlatformObservabilityOptions`), and an OTLP exporter
  pointed at `Otel:Exporter:Endpoint`. One call from each `Ago.Chat` host's own `Program.cs`
  (`7-01`'s Scope). Every manually-instrumented `ActivitySource` this platform or any product built on
  it creates is picked up through one `"Ago.*"` wildcard subscription (`ActivitySourceWildcard`) rather
  than a per-source list this project would otherwise need to keep current - the actual
  platform/product seam for tracing, since this project has no access to a product's source and must
  not need one to know its span names exist.
- Trace context propagation through the outbox and the broker (`messaging.md`'s "the trace id captured
  at write survives the poll-and-publish handoff"): `IOutboxWriter.Enqueue` gains an optional
  `traceContext` parameter (the W3C `traceparent` of the trace an outbox row's event describes,
  captured explicitly by the caller at write time rather than read from an ambient
  `Activity.Current` - a caller that batches several unrelated messages into one physical commit, as
  `Ago.Chat`'s pipeline batch writer does, cannot rely on "whatever is current" without mis-tagging
  every row but the last in a batch), stored on the new nullable `OutboxMessage.TraceContext`/
  `outbox.trace_context` column (`OutboxMessageConfiguration`). `Ago.Platform.Messaging.RabbitMq`'s
  `RabbitMqEventPublisher` now injects the `traceparent` header from whatever `Activity` is current at
  publish time (the caller - an outbox dispatcher, or a fan-out consumer's own ambient activity for a
  second, ephemeral publish - is expected to have already started one, parented from the trace this
  event belongs to); `RabbitMqEventConsumer` extracts it back into a real parent `ActivityContext` and
  wraps every handler invocation in a span named `"{topic} process"` (OTel's own messaging semantic-
  convention shape) - every consumer this platform or a product hosts gets a correctly-parented
  message-processing span for free, from one adapter-level change, without needing to be touched
  individually. Neither `EventEnvelope` nor either messaging port changes for this: propagation is
  transport-level plumbing entirely inside the RabbitMQ adapter, the same discipline `adr/0006`
  already states for everything else this adapter does with headers.
- `Ago.Platform.Realtime.NodeDeliveryConsumer` starts a `node_delivery.dispatch_to_connection` span
  per connection it dispatches to - realtime.md's last fan-out hop, the final stage `nfr.md`'s "traces
  spanning hub -> handler -> DB -> outbox -> broker -> consumer -> delivery" names.

## [0.12.0] - 2026-08-23

### Added

- `Ago.Platform.Resilience` (new project): `ResiliencePolicyBuilder` - a fluent builder over Polly's
  `ResiliencePipelineBuilder` (`WithTimeout`/`WithRetry`/`WithCircuitBreaker`/`WithBulkhead`/`Build`),
  plus `ResiliencePipelineOptions` and its four groups (`ResilienceTimeoutOptions`,
  `ResilienceRetryOptions`, `ResilienceCircuitBreakerOptions`, `ResilienceBulkheadOptions`) bound and
  validated per named pipeline from `Resilience:{pipelineName}:*` via the new
  `AddResiliencePipelineOptions` (`naming-and-structure.md`'s options-binding convention, extended
  with .NET's named-options feature so two pipelines can coexist in one `IServiceCollection` without
  colliding on one unqualified `IOptions<T>`). Replaces the two independently hand-rolled
  `BuildResiliencePipeline()` implementations in `Ago.Platform.Caching.Redis` and
  `Ago.Platform.Storage.S3` (5-04/5-02) - same shape, no shared code, no bulkhead concept in either -
  with one shared builder both now consume; their configured values and observable behaviour are
  unchanged, and their existing tests pass unchanged against it. Bulkhead is the one pattern
  `resilience.md`'s boundary table names that nothing had implemented yet: built on Polly v8's
  rate-limiter strategy (the `Polly.RateLimiting` package, wrapping
  `System.Threading.RateLimiting.ConcurrencyLimiter`) rather than the classic `Polly.Bulkhead`
  package, since `Directory.Packages.props` already pins the lean v8 `Polly.Core`, which does not
  ship a classic bulkhead API. `Ago.Platform.Architecture.Tests`' new `ResilienceLayeringTests`
  asserts `Ago.Platform.Resilience` is the only project that may construct a
  `Polly.ResiliencePipelineBuilder`. Neither `Ago.Platform.Caching.Redis` nor `Ago.Platform.Storage.S3`
  wires up the new bulkhead group - resilience.md's rows for Redis and S3 do not call for one - it
  exists so `6-05`'s webhook dispatcher (the boundary that does need one) composes it from here
  instead of writing a fourth ad hoc implementation (`6-01`).

## [0.11.0] - 2026-08-23

### Fixed

- `Ago.Platform.Abstractions.IEventConsumer.SubscribeAsync` gains a required `consumerName`
  parameter. `Ago.Platform.Messaging.RabbitMq`'s `Competing`-mode queue used to be named after the
  bare topic, with nothing distinguishing "another replica of the same logical consumer" (correct -
  both belong on one shared queue) from "a completely different consumer type that also subscribes
  to this topic" (wrong - each needs its own independent copy of every message). Two or more such
  consumer types silently shared one queue and split its messages between them via RabbitMQ's normal
  competing-consumers dispatch, instead of each receiving every one.
  Found live in `ago-chat`'s `5-11` while verifying widget attachments: `Ago.Chat.Worker`'s
  `UnreadCounterConsumer` and `ConnectionFanoutConsumer` both subscribe `Competing` to
  `MessageAccepted`, and ten operator-sent messages in a row landed entirely on one of the two,
  never the other - real-time message delivery has been unreliable since `3-02` whenever both
  consumers were running, which is the normal case. A regression test
  (`RabbitMqPublishConsumeTests.Competing_TwoDifferentConsumerTypes_BothReceiveEveryMessageIndependently`)
  reproduces the bug against a real broker and passes only with the fix.
  Not a re-exposure of Kafka's own consumer-group mechanics through the port (`adr/0006` still holds
  exchanges/bindings/offsets/partition counts inside the adapters) - `consumerName` is the caller
  declaring *identity*, which `SubscriptionMode.Competing` cannot mean anything without on either
  broker; see the port's own updated doc comment.

### Breaking

- Every `IEventConsumer.SubscribeAsync` call site must now pass a `consumerName` - stable per logical
  consumer type, shared across replicas of that same type, distinct from every other consumer type
  subscribed to the same topic.

## [0.10.0] - 2026-08-22

### Added

- `Ago.Platform.Abstractions.IFileStorage` - presigned direct-to-storage uploads/downloads
  (`adr/0008`), plus `ObjectKey`, `UploadConstraints`, `PresignedUpload`, `ObjectMetadata` and
  `FileStorageUnavailableException`. Implemented in the new `Ago.Platform.Storage.S3` (AWS SDK,
  pointed at MinIO locally via `S3StorageOptions.ServiceUrl`) for `ago-chat`'s `5-02` attachment work.
  Every call runs through retry + per-attempt timeout + circuit breaker (`resilience.md`'s S3/MinIO
  row); a `404` on `GetMetadataAsync` is the expected "does not exist" outcome, excluded from both,
  not a failure. `IFileStorage` corrected `file-storage.md`'s own earlier "Application/Abstractions"
  placement in the same change.

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
