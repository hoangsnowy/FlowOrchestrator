# Changelog

All notable changes to FlowOrchestrator are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
