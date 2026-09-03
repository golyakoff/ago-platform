# Changelog

All notable changes to `Ago.Platform.*` are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning is
[SemVer](https://semver.org/) (`docs/architecture/repositories.md`).

## [0.20.0] - 2026-09-04

### Fixed

- **Every `ago-chat-api` pod restart left a durable queue behind, for ever (`15-15`).** Measured on
  the live broker: 72 `deliver-to-connections.<pod>` queues, 71 of them belonging to pods that no
  longer existed - a running total of every pod the cluster had ever had, each still bound to the
  fanout exchange and routed into on every publish. `NodeDeliveryConsumer` names its
  `Competing`-mode queue after the pod (correct - that is what "deliver to the node holding this
  connection" means), and `RabbitMqEventConsumer` had exactly one shape for `Competing`:
  `durable: true, autoDelete: false`, correct for a genuinely durable subscription and never a
  decision for a queue whose consumer name already names something with no life beyond one process.
  `NodeDeliveryConsumer` now opts into the new `QueueLifetime.ProcessScoped` (below); its main queue
  and retry queue are `exclusive: true, autoDelete: true` and gone the instant the declaring
  connection closes, proven against a real broker
  (`RabbitMqQueueLifetimeTests.ProcessScoped_...`). Its dead-letter queue is unaffected (see
  **Changed**) and, separately, renamed from a per-node name to one shared across every node, so it
  does not reintroduce the identical orphan one queue over - safe only because this consumer never
  actually dead-letters (`MaxAttempts: 1`, and its handler acks even on failure).

### Added

- **`QueueLifetime`** (`Ago.Platform.Abstractions`): `Durable` (the default, and the only shape
  `Competing` had before this) or `ProcessScoped` - a queue that exists only for as long as its
  declaring connection is open. Orthogonal to `SubscriptionMode`: mode is about how a message is
  routed, lifetime is about how long the queue that routing depends on sticks around. See the type's
  own doc comment for when `ProcessScoped` is (and is not) correct - most `Competing` subscriptions
  in this system are genuinely durable and must stay that way, or a rolling deploy silently drops
  whatever was published while no replica happened to be attached.
- **`IEventConsumer.SubscribeAsync` overload taking `QueueLifetime`.** The existing six-argument
  overload is unchanged and keeps compiling for every current caller, forwarding to the new one with
  `QueueLifetime.Durable` - see **Changed** for who this new overload actually affects.

### Changed

- **Source-breaking for a direct `IEventConsumer` implementation, not for a caller.** `IEventConsumer`
  gains a second `SubscribeAsync` overload (above) as a full interface member, not a default parameter
  - a `QueueLifetime`-shaped optional parameter cannot sit before the always-explicit
  `CancellationToken` without either reordering every existing call site or making the token itself
  optional, which this codebase never does. Concretely, this means:
  - **Every existing call site - in this repository and in `ago-chat` - keeps compiling unedited.**
    `ago-chat` consumes `IEventConsumer` only via DI (`grep`-confirmed: no type in `ago-chat` declares
    `: IEventConsumer`), and DI callers only ever reach for the six-argument overload unless they
    explicitly ask for the new one, so `ago-chat` needs nothing from this release but a package-version
    bump once it is packed - no source change, in any of its twelve `SubscribeAsync` call sites.
  - **Anyone who hand-writes an `IEventConsumer` implementation must add the new method.** The only
    two such types exist in this repository - `RabbitMqEventConsumer` (the real adapter) and
    `NodeDeliveryDispatchMetricsTests`' `HandlerCapturingEventConsumer` (a test fake) - and both are
    updated here. No implementation exists in `ago-chat` (confirmed above) or anywhere else known to
    this repository.
- **A subscription's dead-letter queue is never subscription-owned and does not follow
  `QueueLifetime`, even for `ProcessScoped`.** Stated explicitly for the first time here, though
  behaviour for every existing `Durable` caller is unchanged: `retryPolicy.DeadLetterName` is always
  declared `durable: true, exclusive: false, autoDelete: false`, because a dead-letter queue is a
  monitored destination for poison messages regardless of which consumer instance produced them
  (`messaging.md`: "a DLQ with no alert and no runbook entry is a silent data-loss channel") and can
  legitimately be shared by name across independent subscriptions -
  `RabbitMqPublishConsumeTests.Broadcast_TwoConsumers_BothReceiveEveryMessage` (pre-existing, unrelated
  to `15-15`) already relies on exactly that and is what caught the first draft of this fix trying to
  make the DLQ `ProcessScoped` too (a real `RESOURCE_LOCKED` from the broker, not a hypothetical).

## [0.19.0] - 2026-09-03

### Fixed

- **`Ago.Platform.Messaging.RabbitMq.RabbitMqConnection.DisposeAsync` could throw, and leaked its
  lock when it did (`17-09`).** Found by CI on an unrelated, doc-only `ago-chat` PR:
  `RabbitMqConnectionDisposeTests` (`KillingTheConsumerMidBatch`-shaped - a competing consumer with
  deliveries genuinely in flight, not an idle connection) proves both halves against a real broker
  paused mid-dispose, not by reading the code - a plain idle-connection pause was tried first and did
  not reproduce it; the escape only shows up once the client's `DisposeAsync` also has in-flight
  consumer dispatch work to reconcile while the AMQP close handshake is going nowhere. Fixed by
  catching broadly around the client's own `DisposeAsync` and logging a warning rather than
  discarding the failure silently (`resilience.md` update below), and moving `_lock.Dispose()` into a
  `finally` so it always runs, including on the throwing path. `RabbitMqEventPublisher.DisposeAsync`
  had the identical six-line shape around its own `_channel.DisposeAsync()` call and gets the same
  fix here, on the same reasoning (not independently reproduced against a real broker the way the
  connection case was - the code shape and the underlying client's own documented disposal timeouts
  are the basis for it, not a repeated Testcontainers proof).

### Changed

- **Source-breaking for any direct instantiation.** `RabbitMqConnection` and `RabbitMqEventPublisher`
  now take a required `ILogger<T>` constructor parameter (`17-09`), matching every other
  `Infrastructure.*` adapter that already does this (`RedisDistributedLock`, `RedisCache`, `S3FileStorage`,
  ...) rather than a nullable/optional one that would be the only exception to that pattern in this
  codebase. Both types are resolved through DI in every known host (`AddRabbitMqMessaging`), which
  picks up the new dependency automatically wherever logging is already configured - no host-side
  change. Direct `new RabbitMqConnection(...)`/`new RabbitMqEventPublisher(...)` call sites need
  updating to pass `NullLogger<T>.Instance`.

  **Corrected `17-10`, after this shipped**: the sentence here originally said those call sites were
  "this repository's own integration tests", which was wrong and understated the cost. `ago-chat`'s
  own suites construct both types directly too - **38 call sites across 17 files** in
  `Ago.Chat.Integration.Tests` and `Ago.Chat.Concurrency.Tests`, none of which go through
  `AddRabbitMqMessaging`. So moving a consumer to `0.19.0` is not a one-line pin bump; the build fails
  with 41 `CS7036` errors until they are fixed. The DI claim above is still true and is what caused the
  mistake - no *host* changed, and reasoning about hosts is not the same as reasoning about every
  compiler-visible construction. A source-breaking change to a type consumers instantiate has to be
  measured in the consumers, not in the package.
  `Ago.Platform.Messaging.RabbitMq` gains a `Microsoft.Extensions.Logging.Abstractions`
  `PackageReference` for this - already a dependency of `Ago.Platform.Caching.Redis` elsewhere in this
  solution, not a new package to the ecosystem, and the minimal DI logging abstraction rather than a
  hand-rolled reporting mechanism this module would otherwise need to invent on its own.

## [0.18.0] - 2026-08-25

### Changed

- **Breaking, source-breaking for every host that wires telemetry.** `AddPlatformObservability`,
  `PlatformObservabilityOptions`, `OtelExporterOptions`, `ActivitySourceWildcard` and `MeterWildcard`
  move out of `Ago.Platform.Hosting` into a **new package, `Ago.Platform.Observability`** (`7-09`,
  `adr/0046`). The extension method's class is renamed `ServiceCollectionExtensions` ->
  `ObservabilityServiceCollectionExtensions`, because `Ago.Platform.Hosting` keeps a class by the
  former name and a host referencing both packages would otherwise hit `CS0433` on every call site.
  Nothing about the method's behaviour, signature, configuration keys (`Otel:*`) or wildcard values
  changed - only where it ships from.

  **What a consuming host must do:** add `<PackageReference Include="Ago.Platform.Observability" />`
  alongside the existing `Ago.Platform.Hosting` one, and add `using Ago.Platform.Observability;` to
  any file that calls `AddPlatformObservability` or names either wildcard constant. A host that does
  neither fails to compile; there is no silent-behaviour-change failure mode here. A host that never
  wired telemetry in the first place changes nothing at all and simply stops resolving the packages
  below.

- **`Ago.Platform.Hosting`'s declared dependencies drop from ten `PackageReference`s to two**
  (`Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`),
  and its packed `.nuspec` from **16 dependencies to 3**. Gone: the five OpenTelemetry packages and
  the three `Microsoft.Extensions.Options*` packages that existed only for the observability options
  class. The `NU5104` suppression goes with them, onto
  `Ago.Platform.Observability` where the prerelease `OpenTelemetry.Exporter.Prometheus.AspNetCore`
  dependency now lives.

  This is the point of the change, measured rather than asserted. A `Microsoft.NET.Sdk.Worker`
  generic host whose `Program.cs` calls only `AddPlatformKernel()` resolved **39 packages, 8 of them
  OpenTelemetry** (including `OpenTelemetry.Exporter.Prometheus.AspNetCore/1.18.0-beta.1`, which has
  never shipped a stable release) against `0.17.0`, and resolves **30 packages, 0 of them
  OpenTelemetry** against `0.18.0`. Isolating the platform package's own cost - a bare
  `Microsoft.NET.Sdk` project referencing nothing but `Ago.Platform.Hosting` - the same pair reads
  **26 packages -> 5**. Both read out of `project.assets.json` after a clean restore, not inferred.
  The generic host could not have used any of the eight: `AddPlatformObservability` calls
  `AddAspNetCoreInstrumentation()` and `AddPrometheusExporter()`, and a scrape endpoint needs an
  `IEndpointRouteBuilder` a generic host does not have. `Ago.Platform.Hosting` is the one package
  every product host of every shape must reference in order to exist at all - it holds
  `IProductModule` - so one product's dependency had become every product's requirement.

### Added

- `Ago.Platform.Observability` package: `AddPlatformObservability`, its options, and the two
  `"Ago.*"` wildcard constants. Hosts that serve HTTP reference it; generic hosts do not.
- `Ago.Platform.Architecture.Tests.HostingPackagingTests` (`7-09`): asserts `Ago.Platform.Hosting`'s
  `PackageReference` set is exactly the two-package allowlist, that it carries no OpenTelemetry
  dependency, and that `Ago.Platform.Observability` is the only packable project that does. The
  assertion is against the project files, not the compiled assemblies, because the harm is in the
  packed `.nuspec`'s dependency list - which is written from `PackageReference` whether or not any
  type from the package is used in IL. Proven to fail by adding an OpenTelemetry `PackageReference`
  back to `Ago.Platform.Hosting` and reverting it.

### Fixed

- `IProductModule`'s and `AddPlatformObservability`'s XML docs named `Ago.Chat.Api`/`Worker`/
  `Webhooks` as though those were *the* hosts. With a second product, that is simply false - the same
  mistake in prose that the packaging made in metadata. Both now describe host *shapes*, name no
  product, and `AddPlatformObservability` states plainly that it requires a web host and why.

## [0.17.0] - 2026-08-25

`0.16.0` is deliberately skipped here: it is claimed by `5-13`, whose branch was already pushed and
in review when this work started. Two branches taking one version is a collision no merge tool
catches, so `7-08` takes the next one instead of racing for it.

### Changed

- **Breaking.** `Ago.Platform.Abstractions.ILocalConnectionDispatcher.DispatchAsync` now returns
  `Task<DispatchOutcome>` instead of `Task` (`7-08`). Behaviour is unchanged - an unknown connection
  is still a silent no-op, and `NodeDeliveryConsumer` still acknowledges every delivery regardless of
  per-connection outcome - but the implementation now *reports* which of the two happened, which
  nothing could previously tell. The outcome comes from the dispatcher itself rather than from the
  caller checking a proxy for the same fact (the node's `LocalConnectionTracker`, say), because a
  proxy is only correct for as long as every implementation happens to agree with it: the shape of
  defect `7-07` found in the connections gauge. Implementors return `DispatchOutcome.Delivered` when
  the process held the connection and pushed to its transport, `DispatchOutcome.ConnectionNotLocal`
  otherwise.
- **Breaking.** `Ago.Platform.Abstractions.INodeFanoutPublisher.PublishAsync` now returns
  `Task<FanoutResult>` instead of `Task` (`7-08`) - what it resolved, per recipient. Source-compatible
  for callers that simply `await` it; implementors (test doubles, mostly) change. The platform returns
  the numbers rather than recording a metric from them because the dimension that makes them useful
  is which *kind* of principal each recipient is - a visitor with no connection is ordinary, an
  operator with none is not - and "visitor" and "operator" are product concepts the platform must
  never learn (clean-architecture.md's qualifying rule). Deriving a tag from `PrincipalKey`'s text
  would also give the platform an instrument whose cardinality it cannot bound.

### Added

- `Ago.Platform.Abstractions.FanoutResult` / `ResolvedRecipient`: the recipients a fan-out resolved
  and how many live connections the registry had for each. `TotalConnections` is the sum - one
  recipient with three open tabs is three, which is the distinction the fan-out path had no way of
  reporting before.
- `Ago.Platform.Abstractions.DispatchOutcome`: `Delivered` / `ConnectionNotLocal`.
- `Ago.Platform.Realtime.NodeFanoutPublisher` now sets `ago.fanout.recipients`,
  `ago.fanout.connections` and `ago.fanout.nodes` on the span that is already current - the
  `"{topic} process"` span `7-01` starts around the consumer handler that called into it - rather
  than starting a child span of its own. All three are counted off the lists the method has just
  built, so there is no second number that can drift from the first.
- `Ago.Platform.Realtime.RealtimeMetrics`' new counter `ago.platform.realtime.dispatches`, tagged
  `node` (matching the connections gauge, so the two read side by side) and `outcome`
  (`delivered` / `connection_not_local` / `failed`). This is the number that was missing: how many
  of the deliveries a node was handed actually met a connection it still holds.
  `ConnectionDrainCoordinator` - the dispatcher port's other caller - deliberately does **not** feed
  it, so a rolling deploy's `"Reconnect"` pushes never look like a burst of message delivery.

## [0.16.0] - 2026-08-25

### Fixed

- `Ago.Platform.Storage.S3.S3FileStorage.CreateUploadAsync` presigned a plain `PUT` carrying only a
  content type and an expiry: the size the caller declared was captured in `UploadConstraints` and
  then never read by the method at all (`5-13`). A limit the application checks and the storage does
  not enforce is not a limit - a client that declared 1 KiB and then PUT straight at the presigned
  URL was bounded by nothing, and the after-the-fact `HEAD` verification can only refuse to mark an
  object usable, never refuse the write. The declared length is now signed into the URL
  (`GetPreSignedUrlRequest.Headers.ContentLength`): SigV4's canonical request covers every header
  named in `X-Amz-SignedHeaders`, so the store recomputes the signature over the real request's own
  `Content-Length` and answers `403 SignatureDoesNotMatch` before accepting a byte. Confirmed against
  a real MinIO container rather than assumed from AWS's documentation - MinIO honours the signed
  header identically, and both the oversized and the undersized case are pinned by tests that PUT at
  the URL directly, bypassing any application check (`S3FileStorageTests`). Both failed against the
  previous code with `Expected: Forbidden, Actual: OK`.

### Changed

- `Ago.Platform.Abstractions.UploadConstraints.MaxSizeBytes` is renamed to `SizeBytes` and is now an
  *exact* length rather than a ceiling (breaking, for a pre-1.0 package; every known caller
  constructs the record positionally and passes the size it actually intends to upload, so it
  recompiles unchanged). A presigned `PUT` has no way to express "at most N" - a `content-length-range`
  condition exists only in a presigned *POST* policy document, which would mean every browser client
  switching from a raw `PUT` to a multipart form `POST` for the same outcome. The rename exists so the
  port stops promising a ceiling it never enforced and now cannot express; a caller that wants a
  ceiling still checks it before calling, which it had to anyway - storage cannot know a product's own
  quota.

## [0.15.0] - 2026-08-25

### Fixed

- `Ago.Platform.Realtime.RealtimeMetrics`' connections gauge (`ago.platform.realtime.connections`)
  reported how many times `IConnectionRegistry.RegisterAsync` had been called, not how many
  connections a node held (`7-07`). That pairing - increment in `RegisterAsync`, decrement in
  `UnregisterAsync` - would have been correct if `RegisterAsync` were only called on connect, but it
  is also the heartbeat's TTL refresh by design (`ConnectionHeartbeat` re-registers every tracked
  connection every 10s, and the port's own contract says the two are deliberately the same
  operation). Thirteen connections on an idle deployment therefore added ~78 to the gauge per minute
  against a handful of real disconnects: measured live at 564 climbing to 2476 over thirty minutes
  with nobody connected, while Redis held 13 entries. The gauge now *reads the set it describes* -
  `LocalConnectionTracker.Count` at collection time - instead of maintaining a second number
  alongside the calls that maintain the first, which removes the class of drift rather than one
  instance of it. Same shape `ResilienceMetrics`' breaker-state gauge already uses (register a live
  handle, read it in the callback). No consumer of the metric changes; a host that calls
  `AddConnectionRegistry` picks the fix up with no code change of its own.

### Added

- `Ago.Platform.Realtime.RealtimeMetrics.TrackNode(NodeId, LocalConnectionTracker)`: names the
  tracker that answers the connections gauge for a node. `AddConnectionRegistry` calls it when it
  builds the tracker - the composition root is the only place that knows both facts - and it is
  public so a host or test composing the realtime pieces by hand can still say which tracker
  describes which node.
- `Ago.Platform.Realtime.LocalConnectionTracker.Count`: the number of connections this node holds,
  without `Snapshot()`'s whole-dictionary copy.

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
