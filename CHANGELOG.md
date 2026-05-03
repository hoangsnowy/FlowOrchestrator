# Changelog

All notable changes to FlowOrchestrator are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Realtime dashboard via Server-Sent Events.** New `IFlowEventNotifier`
  abstraction in Core (with a no-op default that costs nothing for apps
  without the dashboard) is invoked by the engine on `run.started`,
  `step.completed`, `step.retried`, and `run.completed`. The dashboard
  exposes `GET /flows/api/events/stream` (`text/event-stream`, no
  compression, 15 s heartbeat, optional `?runId=` filter) and replaces
  the 5 s polling loop with `EventSource`-driven push. Polling becomes a
  fallback that activates only when the stream is silent for >20 s or
  reconnects fail 3+ times. Multi-replica deployments stay process-local
  in this release; a Service Bus backplane plugs in via the same
  interface in a later release without engine changes.
- **Page count chip** on the Flows / Scheduled list headers — the plain
  "12 flows" / "1 job" text is now a styled pill with a mono-font number
  in Terracotta and a muted uppercase label.

### Changed

- **Auto-refresh control simplified.** The 5/10/15/30/60 s interval
  dropdown was removed; with realtime push as the primary channel the
  knob only affected the rare polling fallback. The toggle is now a
  single On/Off master switch, and the status displays
  `Live` / `Polling` / `Paused`.

### Fixed

- **Run-termination race in `TryCompleteRunAsync`.** A step that had
  been dispatched but not yet picked up by the consumer left a window
  where neither the runtime status table nor the claim ledger reflected
  it, allowing the run to be marked terminal with the queued step still
  pending. Surfaced as the v1.23.0 publish-CI flake in
  `HappyPathTests.LinearFlow_runs_to_completion` (`Expected 3, Actual 2`).
  `TryCompleteRunAsync` now also consults the dispatch ledger; a
  50-iteration regression stress test pins the behaviour.

## [1.23.0] - 2026-05-03

### ⚠️ Breaking changes

These changes remove or relocate previously public API surface. Strict
semver would request a major bump; this release stays on the minor track
because every change is either a removal of an item already marked
`[Obsolete]` since v1.19 or a relocation of a single trivial helper class.
Recompilation against this version is required for affected callers.

- **Removed obsolete OTel forwarder** in
  `FlowOrchestrator.Hangfire.FlowOrchestratorInstrumentationExtensions`
  (marked `[Obsolete]` since v1.19). Update any
  `using FlowOrchestrator.Hangfire;` followed by
  `tracer.AddFlowOrchestratorInstrumentation()` /
  `meter.AddFlowOrchestratorInstrumentation()` to
  `using FlowOrchestrator.Core.Observability;` — the call site is
  otherwise identical.
- **`InMemoryFlowRepository` moved out of Core** to the
  `FlowOrchestrator.InMemory` project / namespace. Direct callers
  must update their `using` and (if they were referencing it from
  `FlowOrchestrator.Core` only) add a project / package reference to
  `FlowOrchestrator.InMemory`.
- **`Hangfire.AddFlowOrchestrator` no longer auto-registers
  `IFlowRepository`.** Each storage backend's `Use*()` extension is
  now responsible for it; `UseInMemory` / `UseSqlServer` /
  `UsePostgreSql` were updated to register a backend-local
  implementation. Custom `IFlowStore` implementations must register
  `IFlowRepository` themselves — `AddFlowOrchestrator` validates and
  throws `InvalidOperationException` with a guiding message when the
  registration is missing. The previous behaviour silently injected an
  in-process registry from the Hangfire project, which forced an
  unwanted `Hangfire → InMemory` dependency.

### Fixed

- **`Activity` null-dereference (4 sites)** in
  `FlowOrchestratorEngine` when `EnableOpenTelemetry = false` and an
  exception fires inside the protected block. Previously a
  `NullReferenceException` masked the original failure during
  `TriggerAsync`, the `RunStepAsync` handler-failed catch, the
  `RunStepAsync` outer catch, and the When-evaluation path. All four
  call sites now use `activity?.RecordError(ex)` /
  `Activity.Current?.RecordError(ex)`. Three new unit facts in
  `FlowOrchestratorEngineActivityNullSafetyTests` pin each call site.
- **`ForEachStepHandler` slice bounds** at the
  `@triggerHeaders()['…']` resolver. A malformed expression like
  `@triggerHeaders()[']` (length 3) satisfied both
  `StartsWith("['", …)` and `EndsWith("']", …)` and the `[2..^2]`
  slice threw `ArgumentOutOfRangeException`. Added a `Length >= 4`
  guard; the resolver now returns false for malformed inputs and the
  ForEach source falls through to the literal pass-through path.
  `ForEachStepHandlerExpressionEdgeCaseTests` covers seven malformed
  inputs.
- **`PeriodicTimerRecurringTriggerDispatcher.StopAsync` race**
  between cancellation and timer disposal. Disposing the timer
  *before* draining the loop could race
  `WaitForNextTickAsync` into an unobserved `ObjectDisposedException`.
  `StopAsync` now cancels, awaits the loop, then disposes the timer.
- **Webhook rerun + idempotency-key dedup bug.** `POST /api/runs/{runId}/rerun`
  rehydrated the original trigger headers verbatim. If the original
  webhook carried an `Idempotency-Key`, the engine's dedup ledger
  short-circuited the replay and returned the original RunId — making
  the dashboard's "Re-run" button a no-op. The rerun endpoint now
  strips the configured idempotency header (read from
  `FlowRunControlOptions.IdempotencyHeaderName`) before re-invoking
  `engine.TriggerAsync`. Three new regression facts in
  `WebhookRerunIdempotencyKeyHandlingTests` pin both sides of the fix.

### Tests

- Added 11 new regression tests covering: webhook idempotency
  (with-key and distinct-key), When-condition gating concurrency
  (×2), disabled-flow silent skip (×2), ForEach sub-step dispatch
  atomicity (×2), Hangfire dispatcher concurrency (×2), pending
  poll-reschedule + cross-trigger idempotency scoping (×2),
  trigger-body expression error handling (×2), manual retry state
  reset, and webhook rerun + idempotency-key handling (×3).
- Added 2 new unit-test files (Activity null-safety + ForEach
  expression edge cases — 10 facts/theory-rows total).
- Replaced 3 flaky `for (i < 1500) Task.Delay(20)` polling loops in
  `PeriodicTimerRecurringTriggerDispatcherTests` with
  `TaskCompletionSource`-driven 30 s waits per the CLAUDE.md
  anti-flakiness rules.

### Internal

- Removed a stale `FlowOrchestrator.InMemory → FlowOrchestrator.Hangfire`
  `ProjectReference` (zero actual code-level usage — only comment
  mentions). Surfaced and fixed an unrelated transitive-dep
  assumption in `Core.UnitTests` by adding an explicit
  `Microsoft.Extensions.DependencyInjection` package reference.
- Trimmed unused `.claude/skills/` entries (`bootstrap`,
  `add-storage`, `release`); the remaining five (`build-fix`,
  `fixandtest`, `new-flow`, `new-step`, `regression-test`) all map
  to documented CLAUDE.md workflows.

## [1.22.0] - 2026-05-03

### Added

- **`FlowOrchestrator.ServiceBus` — new runtime adapter for Azure Service Bus.**
  Third runtime alongside Hangfire and InMemory: dispatches steps via a shared
  topic (`flow-steps`) with one subscription per registered flow (SQL filter on
  the `FlowId` application property), and runs cron triggers as
  *self-perpetuating scheduled messages* on a dedicated queue
  (`flow-cron-triggers`). Each cron consumer enqueues the next firing as a
  `ScheduledEnqueueTime` message before completing the current one — Service
  Bus's exactly-once-per-tick delivery guarantees no duplicate fires across
  competing replicas, removing the need for DB-backed leader election the way
  the InMemory `PeriodicTimer` model required.

  Wires up via `options.UseAzureServiceBusRuntime(sb => sb.ConnectionString = ...)`
  inside `AddFlowOrchestrator`. Topology can be auto-created at startup
  (`AutoCreateTopology = true`, default; uses `ServiceBusAdministrationClient`)
  or pre-provisioned via IaC for production namespaces lacking Manage rights.
  `Pending` steps with `DelayNextStep` map naturally to
  `ServiceBusMessage.ScheduledEnqueueTime`. The engine's *Dispatch many,
  Execute once* invariant (dispatch ledger + claim guard) makes the at-least-
  once delivery model of Service Bus correct without any new abstractions.

  Local development uses the official Microsoft Service Bus emulator. The
  Aspire AppHost wires it via `AddAzureServiceBus("servicebus").RunAsEmulator()`
  with topic + queue + subscriptions declared programmatically; a fourth
  sample instance (`flow-servicebus`, port 5104) demonstrates the full setup
  alongside the existing SQL Server / PostgreSQL / InMemory instances.

  9 integration tests run against a live emulator container (Testcontainers +
  SQL Edge sidecar): DI wiring (×7), topic round-trip
  (`TriggerAsync` → topic → subscription → handler), and `ScheduleStepAsync`
  delay (`ScheduledEnqueueTime` honoured). All 14 unit tests cover envelope
  serialisation, message-id format, and the disabled-flow processor skip.

### Fixed

- **Engine now rejects `TriggerAsync` for disabled flows.** Previously,
  setting `FlowDefinitionRecord.IsEnabled = false` only stopped the cron
  scheduler — webhooks, manual triggers, and re-trigger requests still
  dispatched steps. Cron-and-step coverage was inconsistent across runtimes
  (Hangfire / InMemory / ServiceBus all had the gap). The fix is a single
  guard at `FlowOrchestratorEngine.TriggerAsync` that consults
  `IFlowStore.GetByIdAsync` and silent-skips when `IsEnabled = false`,
  returning `{ runId: null, disabled: true }` instead of starting a run.
  Emits a structured warning log (EventId 1010
  `TriggerRejectedDisabledFlow`) with `FlowId` + `TriggerKey`, and tags the
  trigger activity with `flow.disabled = true` for trace inspection. Falls
  through to "enabled" when the store has no record yet (e.g. before
  `FlowSyncHostedService` runs on first startup) — safe default.

  Because the gate sits at the runtime-neutral engine layer, it covers all
  three runtimes uniformly. No per-runtime change was needed for Hangfire
  or InMemory — both inherit the fix from the engine.

- **`ServiceBusFlowProcessorHostedService` now skips disabled flows at
  startup.** The new SB runtime creates one `ServiceBusProcessor` per
  registered flow (subscription-per-flow topology). Without this fix, a
  disabled flow would still get an idle processor consuming the connection
  pool. The hosted service now consults `IFlowStore.GetAllAsync()` and skips
  processor creation for any flow with `IsEnabled = false`. Re-enabling at
  runtime requires an app restart for the SB processor to come up — full
  hot-reload is a follow-up item.

- **`ServiceBusRecurringTriggerHub.ScheduleNextAsync` short-circuits for
  unregistered jobs.** Found via QA audit before the v1.22 tag: the cron
  consumer self-perpetuates the next firing *before* invoking the engine,
  so when a flow is disabled mid-flight (or a cron job is `Remove`d via
  `SyncTriggers`) the engine `IsEnabled` gate above would reject every fire
  while the consumer kept enqueuing fresh ticks every cycle — an infinite
  loop of disabled-flow cron messages. Fix: `ScheduleNextAsync` consults
  the in-memory `_jobs` dict (which `SyncTriggers(flowId, false)` already
  clears) and skips enqueuing when the job is unregistered. One orphan
  in-flight scheduled message still fires before the loop terminates;
  the engine gate handles it cleanly. Regression tests in
  `ServiceBusCronDisabledFlowTests`.

- **`StepEnvelope.ToStepInstance` no longer throws on `JsonValueKind.Undefined`
  inputs.** Found via the same audit: a poison message with a hand-crafted
  envelope leaving an input field at `default(JsonElement)` would call
  `Clone()` on an `Undefined` element, throw `InvalidOperationException`,
  and abandon-redeliver until the message hit `MaxDeliveryCount` and
  dead-lettered. Fixed by treating `Undefined` the same as `Null`
  (key present, value null) — same shape handlers see for explicit JSON nulls.

- **Engine claim guard moved from SCHEDULE time to EXECUTE time.** Pre-1.22
  the runtime claim sat in `TryScheduleStepAsync`; this worked under
  Hangfire's and InMemory's 1:1 enqueue→execute assumption but BROKE under
  Service Bus topic broadcast (one enqueue → N consumers, all execute the
  handler because the schedule-time claim was already taken by the original
  scheduler). The claim now sits at the top of `RunStepAsync` where it
  atomically guards execution: first concurrent worker wins, the rest exit
  silently. New `IFlowRunRuntimeStore.ReleaseStepClaimAsync` clears the
  claim row; the engine calls it from the Pending re-schedule path and
  `RetryStepAsync` so the same step can claim again on a fresh attempt.
  Implemented in InMemory, SQL Server, and PostgreSQL stores. Schedule
  becomes purely "enqueue gate" (dispatch ledger), execute becomes purely
  "exclusive run gate" (claim) — clean separation of responsibilities.

  The earlier process-local dedup map in `ServiceBusFlowProcessorHostedService`
  added as a tactical mitigation is removed in this same release; the engine
  refactor obsoletes it. This also fixes the known v2-runtime-claim leak
  on Pending poll re-schedules (`PollableStepHandler` reschedules now work
  without the `PermissiveRuntimeStore` test workaround).

- **`ServiceBusRecurringTriggerHub.ScheduleNextAsync` no longer silently
  swallows enqueue errors.** Discovered by qa-agent E2E audit before the
  v1.22 tag: the previous `try/catch/log` around `ScheduleMessageAsync`
  meant a single transient broker blip (throttle, network interruption,
  namespace failover) silently killed the cron loop until host restart —
  the consumer would still complete the original message even though the
  next fire was never enqueued. Fixed by letting the throw propagate; the
  cron consumer's outer try/catch now wraps the schedule-next call too,
  abandons the message on failure, and Service Bus redelivers the same
  tick for retry. The registration-time call from `RegisterOrUpdate` keeps
  fire-and-forget semantics with a `ContinueWith(OnlyOnFaulted)` log so
  `FlowSyncHostedService` can re-attempt via the next sync cycle without
  bringing down the host on an unobserved exception.

- **Cron drift on consumer backlog fixed.** The cron consumer used to compute
  the next fire from `_timeProvider.GetUtcNow()` (drain time) so a backlogged
  consumer skipped ticks. The next fire is now computed from
  `envelope.ScheduledFor` (the tick that was just consumed), with a
  "now or later" clamp so a deeply-backlogged consumer takes at most ONE
  catch-up tick per drain rather than bursting through hundreds of missed
  ticks. Net effect: a 5 s cron stays on its 5 s cadence even when the
  consumer was briefly slow, instead of becoming "5 s + drain delay" forever.

- **Dashboard `applyRoute` now fires an immediate refresh for non-runs pages.**
  Discovered during a live AppHost investigation: navigating directly to
  `#/flows` or `#/scheduled` (via sidebar click, deep link, or page reload)
  rendered a blank panel until the next 5-second auto-refresh tick. Root
  cause: `applyRoute` only called the per-page loader for the `runs` route;
  every other page relied on the auto-refresh timer, leaving the user
  staring at an empty grid for up to 5 s on every navigation. Fixed by
  calling `refresh({ force: true })` at the end of `applyRoute` for
  overview / flows / scheduled. The Runs branch is unchanged — it still
  invokes `loadRuns` / `selectRun` directly because those have additional
  state to restore from the URL params.

### Changed

- **`FlowOrchestratorEngine` constructor now takes `IFlowStore`.** Required
  dependency for the disabled-flow gate. Transparent for users who construct
  the engine via `AddFlowOrchestrator(...)` (DI auto-resolves), but a
  breaking change for any code that hand-constructs the engine — typically
  test code only. Five existing test files were updated accordingly.
- **`ServiceBusTopologyManager` is no longer `sealed`** and its three
  `Ensure*Async` methods are `virtual`, enabling test seams without a wrapper
  interface. Internal change; no public-API impact.

### Tests

- **3 new unit tests** in `FlowOrchestratorEngineInvariantTests` covering
  the disabled-flow gate: silent-skip with `disabled=true` marker, normal
  dispatch when enabled, and fall-through-to-enabled when the store has no
  record.
- **1 new unit test** in `ServiceBusFlowProcessorSkipDisabledTests` verifying
  that a disabled flow does NOT trigger `EnsureSubscriptionAsync` at startup.
- **10 new integration tests** in `FlowOrchestrator.ServiceBus.IntegrationTests`
  (7 DI wiring + 2 step round-trip + 1 cron round-trip) running against a live
  Service Bus emulator container (Testcontainers + SQL Edge sidecar). The cron
  test fires `* * * * * *` and asserts ≥3 ticks reach the engine within ~30s,
  proving the full schedule→deliver→fire→self-perpetuate loop end-to-end.
- **13 additional unit tests from the QA audit**:
  `ServiceBusCronDisabledFlowTests` (3 — disabled-flow cron self-perpetuation
  short-circuit), `StepEnvelopeEdgeCaseTests` (6 — null/nested/array
  roundtrip, header case-insensitivity, `JsonValueKind.Undefined` handling),
  `ServiceBusStepDispatcherEdgeCaseTests` (4 — `MessageId` collision contract,
  ApplicationProperties string types).

## [1.21.0] - 2026-05-02

### Added

- **`GET /api/runs/timeseries` — server-side time-bucketed run aggregation.**
  Replaces the prior client-side trick where the dashboard pulled the last 500
  raw runs and bucketed them in JavaScript. New endpoint accepts
  `bucket=hour|day`, `hours|days` for trailing windows (or absolute
  `since`/`until` ISO timestamps), and an optional `flowId` filter. Returns a
  contiguous timeline of buckets — including empty ones so the UI can render
  without gap-filling — each carrying `total`, `succeeded`, `failed`,
  `cancelled`, `running`, and `p50DurationMs` / `p95DurationMs`. Window is
  hour/day-aligned at the endpoint so a `hours=24` request always returns
  exactly 24 buckets (the previous mid-bucket `since` produced 25). Caps:
  720h for hour granularity, 365d for day. Implemented across all three
  storage backends (`InMemoryFlowRunStore`, `SqlFlowRunStore`,
  `PostgreSqlFlowRunStore`); SQL stores stream
  `(StartedAt, Status, DurationMs)` rows back and aggregate via the new
  `TimeseriesAggregator` rather than running `PERCENTILE_CONT` server-side.
  Method added to `IFlowRunStore` with a default no-op implementation so
  existing custom stores keep compiling.
- **30-day GitHub-style activity calendar on Overview.** New section below
  the 24h heatmap renders a 7-row × N-column grid keyed by day-bucket run
  volume (5-quantile colour scale, data-relative so a quiet codebase still
  shows contrast). Cells with any failed runs carry a coral underline so
  bad days surface at a glance. Mon/Wed/Fri row labels, hover tooltip with
  exact day + counts, full dark-mode-tuned palette, `prefers-reduced-motion`
  honoured. Uses `bucket=day&days=30` against the new timeseries endpoint.
- **Per-flow health badge on Flow cards.** Each card now shows a state-aware
  health pill computed from a per-flow `bucket=hour&hours=24&flowId={id}`
  fetch: green `100% ok` when all completed runs succeeded, warn `≥90% ok`
  when failure rate is ≤10 %, danger `<90% ok` (with pulsing dot) otherwise.
  The pill flips to a neutral `25 running` when the window has runs but
  nothing has reached terminal state yet — previously this case rendered as
  a misleading "0% ok green". Adjacent stat chips show `p95 {duration}` and
  `{N} runs` for the trailing 24h window.
- **Enhanced Runs pagination.** Replaced the Prev/Next-only footer with:
  numbered page buttons + ellipsis (`1 … 4 [5] 6 … 9`), page-size selector
  (10 / 20 / 50 / 100, persisted to localStorage and the `size` URL param),
  jump-to-page input on wider viewports, keyboard navigation
  (`←/→`/`Home`/`End` on the pagination region), bold-emphasis count display
  (`Showing 41–50 of 84`), and full ARIA wiring (`role="navigation"`,
  `aria-current="page"`, `aria-label`s). Page size is anchored on the
  current first row when changing — the user keeps the row they were
  looking at, the surrounding window resizes around it. Deep-linkable via
  `#/runs?size=50&page=2`.

### Changed

- **Dashboard Overview now consumes the timeseries endpoint instead of
  pulling raw run history.** `loadOverview` issues two `Promise.all`
  requests (`bucket=hour&hours=24` + `bucket=day&days=30`) replacing the
  prior `take=500&since=24h_ago` trick. Same goes for `loadFlows`, which
  fans out one per-flow timeseries call instead of bucketing a shared
  500-run response client-side. Net effect: backend does the aggregation
  once with proper indexes, client stops shipping kilobytes of raw run
  rows over the wire just to count them.

### Tests

- 4 unit tests in `InMemoryFlowRunStoreTests` covering hourly buckets +
  percentile interpolation (NIST type 7), `flowId` filter exclusion, empty
  windows returning zero-filled buckets, and 30-day daily aggregation.
- 5 integration tests in `DashboardTimeseriesEndpointTests` covering
  default-24h response shape, `bucket=day` granularity dispatch, `flowId`
  filter pass-through, the 720h safety clamp on absurd `hours` values,
  and `Vary: Accept-Encoding` emission per the dashboard endpoint contract.

## [1.20.0] - 2026-05-02

### Added

- **Design-system token palette inlined in the dashboard.** `DashboardHtml.cs`
  now ships the full `--fo-*` token set from
  `.claude/designs/FlowOrchestrator Design System/colors_and_type.css`
  (terracotta/parchment/ivory/warm-sand/etc.), plus the type, radius, and
  ring-shadow tokens. Existing `--bg`, `--accent`, `--surface`, ... names are
  kept as back-compat aliases so the 197 `var(--*)` usages in the rest of the
  file keep working unchanged. Source Serif 4 was added to the inline Google
  Fonts import so future serif-driven UI (cost cards, run titles, etc.) needs
  no extra network request.
- **Dashboard SPA assets split into embedded resources.** `DashboardHtml.cs`
  shrunk from 2103 to 131 lines. The HTML shell, CSS, and JS now live as three
  embedded resources (`Assets/index.html`, `Assets/dashboard.css`,
  `Assets/dashboard.js`) loaded once at static-init and inlined into the
  cached template. Output HTML is byte-identical to the previous monolithic
  raw-string-literal version (verified by 246 dashboard integration tests
  before-and-after across net8/9/10).
- **Pre-compressed dashboard root response.** The dashboard root page is
  now rendered once at startup and cached in three forms — raw UTF-8, Brotli,
  and Gzip — via the new `PrecompressedDashboardPage` type. The `MapGet("")`
  handler picks the best variant based on the inbound `Accept-Encoding` header,
  honors `q=0` overrides per RFC 7231 §5.3.4, and emits `Vary: Accept-Encoding`
  on every response so caches key the variants correctly. Typical dashboard
  payload drops from ~80 KB to ~12 KB on the wire (≥4× reduction asserted in
  tests). Compression cost is paid once at process start, never per request.
- **Expression-resolution fast paths and process-wide parse cache.**
  `StepOutputResolver.IsStepExpression` and the `TriggerExpressionResolver`
  helpers now reject non-`@` strings with a single character walk — sub-1 ns,
  zero allocation — so the resolver can be called against every input value
  cheaply. `StepOutputResolver` keeps a `ConcurrentDictionary` parse cache for
  successful regex matches; first call parses, subsequent calls do a dictionary
  lookup. Duplicated `TryResolveTrigger{Body,Headers}Expression` private statics
  in `DefaultStepExecutor` now delegate to the canonical
  `TriggerExpressionResolver` (single source of truth). See
  [docs/benchmarks/expression-resolver-2026-05-02.md](docs/benchmarks/expression-resolver-2026-05-02.md)
  for measured numbers.
- **`tests/benchmarks/FlowOrchestrator.Benchmarks` project (new).** First
  BenchmarkDotNet harness in the repo, with the expression-resolver suite as
  its first set of cases. `FlowOrchestrator.Core` exposes internals to the
  benchmark assembly via `InternalsVisibleTo` so internal helpers can be
  measured without expanding the public API surface.
- **`perf-agent` specialist agent definition** at
  `.claude/agents/perf-agent.md`. Twelve performance bug-class categories
  (P1 Allocation through P12 Threading), explicit hard rules around
  no-new-deps / no-public-API / no-wire-format / no-unsafe /
  no-disabling-telemetry, and a two-agent collaboration section to
  coordinate with `qa-agent` on overlapping concurrency concerns.
  Reusable across sessions — invoke before tagging or after large
  refactors. Surfaced findings F1–F9 during the post-1.20.0 audit;
  F1 (InMemoryFlowRunStore secondary indexes), F2 (FlowGraphPlanner
  memoisation), and F7 (ForEachStepHandler dedupe) are deferred to a
  follow-up release that ships dedicated benchmarks for the engine
  hot path.

### Performance

- **Dashboard JSON responses write directly to the response Stream.**
  `WriteJsonAsync` previously round-tripped through an intermediate
  UTF-16 string (`JsonSerializer.Serialize` followed by
  `response.WriteAsync(string)`). For a typical /api/runs response
  (~60 KB) the intermediate allocation dominated the request's GC
  pressure. Now uses
  `JsonSerializer.SerializeAsync(response.Body, value, opts)` — ~10×
  lower per-request allocation on larger endpoints. Wire format is
  byte-identical.
- **DefaultStepExecutor pre-scan for clean inputs.** `ResolveInputs` no
  longer allocates a fresh `Dictionary<string, object?>` when none of
  the step's input values could possibly need expression resolution
  (the common static-input case). A pre-scan checks for `@`-prefixed
  strings, nested `JsonElement`s, dicts, or sequences; when none are
  present, the original IDictionary is returned unchanged.
- **Allocation-free flow lookup at dispatch.** Replaced
  `flows.FirstOrDefault(f => f.Id == flowId)` at the engine and
  Hangfire call sites with the new `FlowRepositoryExtensions.FindById`
  extension — a plain for-loop over
  `IReadOnlyList<IFlowDefinition>`. Eliminates the closure capture
  allocated on every step dispatch.
- **Dashboard JSON endpoints now honor `Accept-Encoding`.**
  Previously only the static HTML root page (`GET /flows`) was
  compressed; every JSON API endpoint (`/api/flows`, `/api/runs`,
  `/api/runs/{id}`, etc.) emitted raw bytes regardless of what the
  client requested. The dashboard SPA polls `/api/runs` every 5 s and
  payloads commonly land between 5 KB and 60 KB — cumulative bandwidth
  waste over a long session was significant. `WriteJsonAsync` now wraps
  the response stream in a `BrotliStream` (or `GZipStream` fallback) at
  `CompressionLevel.Fastest` based on the inbound `Accept-Encoding`
  header, emits `Vary: Accept-Encoding` on every response, and reuses
  the existing RFC 7231 q-value parser. Wire format is byte-identical
  to the uncompressed variant. Measured ~5× reduction on a 50-run
  list payload (test
  `ApiRuns_BrotliPayload_IsSignificantlySmallerThanRaw` asserts a
  conservative 3× minimum). See
  [docs/benchmarks/dashboard-json-compression-2026-05-02.md](docs/benchmarks/dashboard-json-compression-2026-05-02.md).
- **Engine termination check in a single pass.** The classifier that
  decides Run.Status at the end of a flow ran three separate
  `LINQ.Any` passes over `statuses.Values` plus a
  `Where + ToHashSet + All` chain to derive the leaf set. Coalesced
  into a single foreach with three booleans plus an allocation-free
  `IsLeaf` helper.
- **InMemoryFlowRunStore: per-run secondary indexes eliminate O(n)
  global scans.** Engine hot-path reads (`GetStepStatusesAsync`,
  `GetClaimedStepKeysAsync`, `GetDispatchedStepKeysAsync`,
  `GetRunDetailAsync`) used to scan the global flat
  `ConcurrentDictionary` keyspace with a `Where(k => k.RunId == runId)`
  filter — O(total_steps_in_history) per call, called 2× per step
  completion. Three new secondary indexes
  (`_stepKeysByRun`, `_claimsByRun`, `_dispatchesByRun`) hold per-run
  sets of step keys, maintained alongside the flat dictionaries on every
  write. Engine reads now run in O(steps_in_run) — asymptotic
  improvement, not just constant-factor. Measured: at 10,000 runs in
  store, `GetStepStatusesAsync` drops from **1.5 ms / 393 KB allocated**
  to **744 ns / 592 B** (2,059× faster, 663× less allocation). At 1,000
  runs the speedup is 70×; at 100 runs, 13×. Per-call cost stays flat
  regardless of total run history. Full table:
  [docs/benchmarks/inmemory-store-runscale-2026-05-02.md](docs/benchmarks/inmemory-store-runscale-2026-05-02.md).
- **FlowGraphPlanner: cache the manifest sorted-key list per flow.**
  `BuildKnownStepKeys` previously rebuilt a fresh `SortedSet<string>`
  from `flow.Manifest.Steps.Keys` and called `ToArray()` on every
  Evaluate (which itself runs on every step completion). The manifest
  is immutable post-startup, so the sorted key array is now computed
  once per flow via a `ConditionalWeakTable<IFlowDefinition,
  ManifestKeyCache>` and reused. For linear flows without loops or
  foreach (the common case), the cached array is returned directly — no
  SortedSet allocation, no per-call sort. Flows with runtime scope
  expansion fall back to the original full-build path with identical
  semantics. Measured: 2-3× faster Evaluate across all manifest sizes;
  allocation reduction grows with manifest size (3.6× at 5 steps, 10.2×
  at 100 steps). Full table:
  [docs/benchmarks/flowgraph-planner-2026-05-02.md](docs/benchmarks/flowgraph-planner-2026-05-02.md).

### Conscious deferrals

These items were evaluated during the post-1.20.0 audit and consciously
deferred to a follow-up release:

- **F7 (ForEachStepHandler trigger-helper dedupe).** The local
  `TryResolveTriggerBodyExpression` and `TryResolveTriggerHeadersExpression`
  in `ForEachStepHandler` look like duplicates of the canonical
  `TriggerExpressionResolver` helpers but have intentionally different
  semantics — when the path remainder is empty, the canonical path
  wraps `triggerData` via `ExpressionPathHelper.ToJsonElement`, while
  the ForEach local copy returns the raw object so collection-typed
  triggers iterate without an extra serialise round-trip. Deduping
  would change observable behaviour. A safer cleanup is to introduce
  an opt-out parameter on the canonical helper; that's a v1.21
  follow-up with parity tests.
- **F8 (SqlFlowRunStore per-dispatch connection count).** Each engine
  step opens 4-7 fresh `SqlConnection` instances. The agent's initial
  framing implied this was wasteful, but `Microsoft.Data.SqlClient`
  pools connections by default — opening a "fresh" connection acquires
  one from the pool in microseconds. The real opportunity is *batching
  reads* (a single query returning step statuses + claimed + dispatched
  + control state instead of three round-trips). That's a coordinated
  storage-API change, not a local refactor, and is gated on an
  integration benchmark which we don't yet have. Deferred.
- **F9 (StepCollection.FindStep nested-key allocation).** The
  `string.Split('.')` allocates a `string[]` only on the nested-key
  path (e.g. `"loop.0.child"`), which is rare. The recursive search
  needs string keys for `Dictionary.TryGetValue`, so a span-based
  parser would still need to allocate substrings — no measurable win
  even at scale. Below the noise floor; left as-is.

### Security

- **Constant-time webhook secret comparison.** The webhook handler at
  `DashboardServiceCollectionExtensions.cs` previously used
  `string.Equals(providedKey, expectedSecret, StringComparison.Ordinal)`,
  which short-circuits at the first differing character — an attacker
  could recover the secret one byte at a time through response-time
  measurement. The handler now delegates to the existing `SecureEquals`
  helper (`CryptographicOperations.FixedTimeEquals` over UTF-8 bytes),
  the same primitive used for the dashboard's BasicAuth path. Severity:
  medium-high — webhook secrets are typically high-entropy strings, but
  the comparison sits on the request hot path and is the dashboard's
  advertised webhook-authentication mechanism.

### Tests

- 13 new unit tests for the `StepOutputResolver` fast path and parse
  cache: boundary inputs (null, empty, whitespace, `@`, `@step`,
  `@steps(`, leading whitespace, uppercase prefix, non-`@` literal);
  64-task concurrent resolution against a shared static parse cache;
  cross-instance cache equivalence; whitespace normalisation.
- 13 new dashboard integration tests: 100-parallel-request
  byte-identical hashes for Brotli + Gzip; pre-compressed buffer
  immutability before and after a concurrent burst; webhook security
  hardening (slug case-insensitivity, secret case-sensitivity,
  lowercase `bearer` prefix, structural regression for the
  timing-attack fix); RFC 7231 q-value parser edge cases (`q=1.0`,
  `q=0.0`, negative q, `*` wildcard, duplicate-coding first-wins,
  OWS tolerance).
- Total: 26 new tests × 3 frameworks = 78 new test runs.

## [1.19.0] - 2026-05-01

### Added

- **Production-grade observability across logs, traces, and metrics.** Three-phase
  upgrade in this release:
  - **Phase 1 — Foundations + P0 correctness.** A new `FlowOrchestrator.Core.Observability`
    namespace gathers `FlowOrchestratorTelemetry` (moved from `Core.Execution`),
    `FlowOrchestratorInstrumentationExtensions` (moved from `FlowOrchestrator.Hangfire`,
    Hangfire keeps a deprecated `[Obsolete]` shim for one release), `LogEvents` event-ID
    constants, an `EngineLogScope` helper, and an `ActivityExtensions.RecordError` helper
    that follows the OTel exception semantic convention. Every public engine entry point
    now opens a `BeginScope({RunId, FlowId, StepKey, Attempt})` so nested log lines —
    including logs emitted by user-written step handlers — are correlation-ready. Every
    failed step / failed trigger now marks its activity with `ActivityStatusCode.Error`
    and records the exception, so APMs treat the span as red instead of silently green.
  - **Phase 2 — Trace continuity across runtime boundaries (Hangfire AND InMemory).**
    A new `TraceContextHangfireFilter` (`IClientFilter` + `IServerFilter`) captures
    `Activity.Current.Context` on enqueue, persists the W3C `traceparent` /
    `tracestate` as Hangfire job parameters, and restores them in a wrapping
    `flow.runtime.execute` activity when the worker dequeues. Symmetrically, the
    InMemory runtime captures the same context on `InMemoryStepDispatcher` enqueue
    (stored on `InMemoryStepEnvelope.ParentTraceContext`) and the channel runner
    opens an equivalent `flow.runtime.execute` wrapper before invoking
    `RunStepAsync`. Inbound webhook (`POST /flows/api/webhook/...`) and signal
    (`POST /flows/api/runs/{runId}/signals/{name}`) endpoints in the Dashboard
    read the same headers via the new `InboundTraceContext` helper and start their
    entry-point activity as a child. The upshot: a single `traceId` connects the
    original caller, the run, the runtime job, and every step span — for both
    runtimes — no more forests of disconnected root spans. `FlowOrchestratorTelemetry.SharedActivitySource`
    is now public so external runtime adapters can emit through the same source.
  - **Phase 3 — Full span coverage, all metrics wired, source-gen logging, E2E test.** Three
    new spans: `flow.step.retry` (per `RetryStepAsync`), `flow.step.when` (per `When`-clause
    evaluation, tagged with resolved expression + result), `flow.step.poll` (per iteration
    in `PollableStepHandler`, tagged with attempt number and condition match). Five new
    instruments fully wired: counter `flow_step_retries`, counter `flow_step_skipped`
    (with `reason` tag — `when_false` / `prerequisites_unmet`), counter
    `flow_step_poll_attempts`, histogram `flow_signal_wait_ms` (from waiter registration
    to signal delivery, recorded by `FlowSignalDispatcher`), histogram `flow_cron_lag_ms`
    (scheduled-fire-time vs actual-dispatch, recorded by both Hangfire and InMemory
    recurring dispatchers, tagged with `runtime`). Engine hot-path `ILogger` calls converted
    to source-generated `[LoggerMessage]` partial methods (`EngineLog.cs`) for zero-allocation,
    AOT-friendly logging. New E2E integration test
    `TraceContextPropagationTests` spins up a real `BackgroundJobServer` over Hangfire
    in-memory storage and asserts the parent `TraceId` survives enqueue → dequeue.
- **`AddFlowOrchestratorHealthChecks()` extension** on `IHealthChecksBuilder`.
  Registers a storage-reachability probe (`flow-orchestrator-storage`) backed by
  `IFlowStore.GetAllAsync()` so a load balancer or container orchestrator can drop
  traffic when the database is unreachable. Probe budget defaults to 5 s and is
  configurable; the resolved `IFlowStore` is fetched from DI on every probe so
  SQL Server, PostgreSQL, and in-memory all work without re-registration.
- **Documentation: *Observability*** ([docs/articles/observability.md](docs/articles/observability.md))
  rewritten with the full per-span / per-metric / per-tag table, a "Distributed tracing
  across the runtime" diagram, sampling guidance, and a logger-scope / EventIds section.
- **Documentation: *Versioning Flows in Production*** ([docs/articles/versioning.md](docs/articles/versioning.md)).
  Five-section guide covering the three-layer mental model (Id / Version / manifest),
  safe changes, caution changes that require a drain, breaking changes with
  migration recipes, a pre-deploy checklist, and a worked `OrderFulfillmentFlow`
  v1.0 → v1.1 → v2.0 evolution.
- **Documentation: *Production Deployment Checklist*** ([docs/articles/production-checklist.md](docs/articles/production-checklist.md)).
  Six-section checklist covering storage and persistence (the twelve tables
  written by `FlowOrchestratorSqlMigrator`), multi-instance deployment under the
  *Dispatch many, Execute once* invariant, monitoring (logs / OTel spans / metrics
  / suggested alerts / health-check endpoint), secrets and PII, capacity planning
  with retention guidance, and the upgrade path.
- README: new **Production?** top-level section and two doc-table rows linking
  to the new pages.

### Changed

- `FlowOrchestratorTelemetry` moved from the `FlowOrchestrator.Core.Execution` namespace
  to `FlowOrchestrator.Core.Observability`. Add `using FlowOrchestrator.Core.Observability;`
  to existing call sites; binary compatibility is unaffected because the type is normally
  resolved from DI.
- `AddFlowOrchestratorInstrumentation` moved from `FlowOrchestrator.Hangfire` to
  `FlowOrchestrator.Core.Observability`. The Hangfire namespace still exposes a forwarding
  `[Obsolete]` shim that emits a CS0618 warning at the call site. Existing user code
  continues to compile; update the namespace at your convenience.

### Internal

- `OpenTelemetry.Api` (~30 KB API-only package) added as a direct dependency of
  `FlowOrchestrator.Core` to let the OTel registration helpers ship in the same assembly
  that already emits the activities. The full SDK is still opt-in.

## [1.18.0] - 2026-05-01

### Added

- **`WaitForSignal` built-in step type for human-in-the-loop workflows.** A step parks
  in `StepStatus.Pending` until an external HTTP signal is delivered, then resumes with
  the signal's JSON payload as its output — available to downstream steps via
  `@steps('wait_for_approval').output.fieldName`. This unlocks the entire approval
  workflow category (order fulfillment, content moderation, contract sign-off, manual
  QA gates) without forcing users to fake it via long-polling steps.
  - New `IFlowSignalStore` abstraction (Core) with implementations for in-memory, SQL
    Server, and PostgreSQL. Migrations add `FlowSignalWaiters` / `flow_signal_waiters`
    idempotently on existing databases — no data loss.
  - New `IFlowSignalDispatcher` service plus `POST /flows/api/runs/{runId}/signals/{signalName}`
    endpoint with the documented status-code matrix (200 / 400 / 404 / 409).
  - Dashboard run-detail page renders a **Send Signal** button on every parked
    `WaitForSignal` step; the button auto-resolves the configured signal name from the
    step input so users do not need to know it.
  - Optional `timeoutSeconds` input transitions the step to `Failed` with a descriptive
    reason when no signal arrives in time. The handler is idempotent across stale
    re-dispatches: leaving the waiter row as a tombstone after delivery / expiry
    prevents the polling loop from re-registering a fresh waiter.
  - Sample: `samples/FlowOrchestrator.SampleApp/Flows/ApprovalWorkflowFlow.cs`.
  - Documentation: [WaitForSignal](docs/articles/wait-for-signal.md).

## [1.17.0] - 2026-05-01

### Added

- **`When` boolean condition on `RunAfter`**. Steps can now branch on
  *values* of prior step outputs or the trigger payload, not just on predecessor
  status. A step whose `When` evaluates to `false` is recorded as `Skipped`
  (not `Failed`), and the dashboard's new **"Why skipped"** panel shows the
  evaluation trace (e.g. `500 > 1000 → false`) so authors can debug branching
  decisions without log diving.
  - `RunAfterCondition` carries optional `Statuses` and `When`. The legacy
    `["x"] = [StepStatus.Succeeded]` array literal continues to compile via
    `[CollectionBuilder]` + an implicit conversion — no source changes required
    for existing flows.
  - Hand-rolled recursive-descent `BooleanExpressionEvaluator` supports `==`,
    `!=`, `>`, `<`, `>=`, `<=`, `&&`, `||`, `!`, parentheses, and literals
    (number, string, `true`, `false`, `null`). Zero new third-party dependencies.
  - LHS resolution reuses the existing `StepOutputResolver` (Plan 01) plus
    extracted `@triggerBody()`/`@triggerHeaders()` helpers.
  - SQL Server / PostgreSQL / in-memory storage all gain a nullable
    `EvaluationTraceJson` column (idempotent `ALTER TABLE` migration).
  - Sample: `samples/.../Flows/AmountThresholdFlow.cs`.
  - Documentation: [Conditional Execution](docs/articles/conditional-execution.md).

## [1.16.0] - 2026-04-30

### Added

- **`FlowOrchestrator.Testing` package** — fluent in-process test host for flow integration tests
  without ASP.NET, Hangfire, or a real database.
  - Single-line setup: `await FlowTestHost.For<MyFlow>().WithHandler<H>("Step").BuildAsync()`.
  - Builder options: `WithService<T>(fake)`, `WithHandler<T>(stepType)`, `WithLogging`,
    `WithSystemClock` (frozen clock for cron tests), `WithFastPolling` (collapses
    `pollIntervalSeconds` to ~100ms), `WithCustomConfiguration` (escape hatch).
  - `TriggerAsync` and `TriggerWebhookAsync` poll `IFlowRunStore` until the run reaches a
    terminal status (or the test-host timeout fires). Default timeout: 30 seconds.
  - `FlowTestRunResult` exposes `Status`, per-step `Output`/`Inputs`/`AttemptCount`/`FailureReason`,
    and the persisted `Events` log.
  - Documentation: [Testing](docs/articles/testing.md).

## [1.15.0] - 2026-04-30

### Added

- **Mermaid diagram export** (`FlowOrchestrator.Core.Diagnostics.FlowMermaidExporter`).
  Convert any `IFlowDefinition` or `FlowManifest` into a Mermaid `flowchart`
  string with `flow.ToMermaid()`. Output renders directly in GitHub READMEs,
  Notion, Confluence, and any modern Markdown surface.
  - New `MermaidExportOptions` for direction, trigger inclusion, type display, and styling.
  - Sample app now accepts `--export-mermaid <flowId|flowName>` to print the
    diagram and exit — useful for CI workflows that comment the new shape on PRs.
  - Dashboard exposes a "Mermaid" tab with a Copy button on every flow detail
    page, served by `GET /flows/api/flows/{id}/mermaid` (`text/plain`).
  - Documentation: [Mermaid Diagram Export](docs/articles/mermaid-export.md).
