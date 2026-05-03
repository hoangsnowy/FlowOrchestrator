# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Workflow Rules

- **Always execute immediately** — never stop at planning. Make file changes first, then describe what was done. If asked to implement something, write the code now, don't outline steps.
- **Always confirm completion explicitly** — after finishing work, state: what files were changed, build status, and test results.
- **Always write tests** — any bug fix or new feature must include unit tests. Do not wait to be asked. Run them and confirm they pass.
- **Always verify the build** — run `dotnet build` after changes. If errors exist, fix them and rebuild. Do not report completion until the build is clean.
- **Always add XML doc comments** — every new file must have `///` XML doc comments on all public types and members. Follow the Documentation Standards section below.

## Testing

- Tests live in `./tests/` split into three categories — see `tests/README.md` for the full picker:
  - **`tests/unit/`** — `*.UnitTests` projects. Pure NSubstitute mocks, no I/O, no `Task.Delay > 50ms`, no containers, no HTTP. Total wall-clock target: **< 60 s**. Runs on every PR and push.
  - **`tests/integration/`** — `*.IntegrationTests` projects. Hits real DB via Testcontainers, `WebApplicationFactory`, Hangfire in-memory, etc. Runs on every PR + push.
  - **`tests/regression/FlowOrchestrator.RegressionTests/`** — timing-sensitive (cron / polling / timeout) and concurrency stress (64-task gate contests). Runs nightly + on push to `main` + manual dispatch. Has its own `xunit.runner.json` with parallelization disabled.
- Framework: **xUnit + NSubstitute**. Use plain xUnit `Assert.*` — do **not** add FluentAssertions, Shouldly, or any fluent-assertion library.
- **AAA pattern** is mandatory: every `[Fact]`/`[Theory]` body must contain three comment blocks, in order — `// Arrange`, `// Act`, `// Assert`. Shared fixture setup in fields/constructor is allowed and does not count as the body's Arrange (a body block may be empty if there is genuinely nothing to do).
- **Solution filters** — pick the right one for what you're running:
  - `dotnet test FlowOrchestrator.UnitTests.slnf` — fast feedback (~30 s).
  - `dotnet test FlowOrchestrator.IntegrationTests.slnf` — needs Docker.
  - `dotnet test FlowOrchestrator.RegressionTests.slnf` — slow, run before merging anything that touches scheduling, polling, or concurrency primitives.
- **Anti-flakiness rules** (mandatory for any test you write):
  - Never assert an *upper bound* on `Stopwatch.Elapsed` — that pattern is the classic source of CI flakiness.
  - Never poll on a counter with a wall-clock deadline (`while (counter == 0) Task.Delay(...)`). Wait on a logical event — `TaskCompletionSource` set by the handler / store / dispatcher.
  - When you need a real sleep budget (timeout assertions), pick a generous one (≥ 30 s) so CI CPU contention doesn't trip it.
- New tests go in the matching project, picked by category first: a pure unit test for `FlowOrchestrator.Core` lives in `tests/unit/FlowOrchestrator.Core.UnitTests`; an integration test for the same component lives in `tests/integration/FlowOrchestrator.{Component}.IntegrationTests`.

## Build & Verification

- Before reporting a task complete: `dotnet build` must show 0 errors, 0 warnings (or document why warnings are acceptable).
- If build fails after a fix, keep iterating — do not stop and ask unless truly stuck after 3+ attempts.

### HTTP endpoints — small things AI keeps forgetting

When adding ANY HTTP endpoint that returns >1 KB typical payload (HTML page, JSON API, file download, etc.), verify the following before declaring the task complete:

1. **Honor `Accept-Encoding`** — return Brotli when client sends `br`, Gzip otherwise, raw if neither. Don't compress only the root page; JSON endpoints are hit far more often by the dashboard's 5 s auto-refresh loop.
2. **Emit `Vary: Accept-Encoding`** on every response so CDNs / browser caches / reverse proxies key the variants correctly.
3. **Add a test** asserting that the compressed variant decompresses to the same bytes as the uncompressed variant. Cheap insurance against accidentally double-compressing or skipping the wrap.
4. **Pick the right compression level** — `CompressionLevel.Optimal` for *pre-compressed* static pages (one-time CPU at startup); `CompressionLevel.Fastest` for *per-request* dynamic responses (the encoder runs on every hit).

If you're modifying `DashboardServiceCollectionExtensions.WriteJsonAsync` or `WriteCompressedHtmlAsync`, this checklist applies. If you're adding a new endpoint that uses neither, the same rules still apply — implement them inline.

### Dashboard endpoints — DI parameters that integration tests don't register

Integration tests (`tests/integration/FlowOrchestrator.Dashboard.IntegrationTests`) bootstrap the dashboard via a minimal `DashboardTestServer` that intentionally **bypasses `AddFlowOrchestrator`** — it registers only the handful of services the dashboard endpoints directly need (Hangfire in-memory, fake `IFlowStore`, etc.). Adding a new constructor / minimal-API parameter pulled from DI silently breaks every test that exercises that endpoint:

- Minimal-API parameter binding treats unknown service types as required `[FromServices]` and **400s the request** with no clear error.
- The 400 surfaces as `Assert.Equal(OK, BadRequest)` in 4+ Dashboard tests at once — easy to misread as a flake on first sight.

When adding/changing a Dashboard endpoint signature:

1. **Resolve optional services from `HttpContext.RequestServices` with a default**, e.g.
   `var opts = http.RequestServices.GetService<FlowRunControlOptions>() ?? new();`
   instead of taking `FlowRunControlOptions opts` directly as a handler parameter.
2. If the service is genuinely required (engine, flow store), it's already registered by every test — no fallback needed.
3. **Verify locally** before pushing: `dotnet test tests/integration/FlowOrchestrator.Dashboard.IntegrationTests/...` (no Docker, ~3 s).

This applies to **every minimal-API endpoint in `DashboardServiceCollectionExtensions.cs`**, not just rerun.

### Integration-test flake taxonomy

Two recurring flake patterns have caused red CI runs that turned green on retry. Treat any new test that triggers either pattern as a bug — fix it, do not just rerun.

1. **Time-boundary flakes** — assertions that depend on `DateTimeOffset.UtcNow` falling inside a specific bucket (hour, minute, day). Fail at exactly `XX:00` when the bucketing crosses a boundary mid-test. Fix: snap your input timestamps to a known boundary (truncate to hour / floor / inject a `TimeProvider`), don't rely on wall-clock alignment.
2. **NSubstitute `Received(N)` race on async/fire-and-forget paths** — asserting `mock.Received().CompleteRunAsync(runId, "Failed")` immediately after dispatching work that completes asynchronously. The call may not have landed yet when the assertion fires. Fix: wait on a logical event (`TaskCompletionSource` set inside the `When().Do(_ => tcs.TrySetResult())` callback) before asserting received-call counts. The 30 s `WaitAsync` budget from `tests/regression` rules applies here too.

When CI shows red on a PR, FIRST inspect the failure output — do not reflexively assume flake. The ratio in this repo's history is roughly 1:1 between "real regression hidden as flake" and actual flake. Look at the test names and assertion messages before retrying.

## Commands

```bash
# Build
dotnet build
dotnet build --configuration Release

# Run tests by category (see "Testing" section above)
dotnet test FlowOrchestrator.UnitTests.slnf            # fast — every change
dotnet test FlowOrchestrator.IntegrationTests.slnf     # needs Docker
dotnet test FlowOrchestrator.RegressionTests.slnf      # slow — before merging

# Run a single test project
dotnet test ./tests/unit/FlowOrchestrator.Core.UnitTests/FlowOrchestrator.Core.UnitTests.csproj

# Filter to a specific test class or method
dotnet test --filter "FullyQualifiedName~MyTestClass"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory ./test-results

# Local development (requires Docker Desktop — spins up SQL Server via .NET Aspire)
dotnet run --project ./FlowOrchestrator.AppHost/FlowOrchestrator.AppHost.csproj
```

Libraries target `net8.0`, `net9.0`, and `net10.0` (set in `Directory.Build.props`). Sample projects target the latest SDK available.

## Architecture

FlowOrchestrator is a .NET library for orchestrating workflows from **declarative code manifests**, executed by a **pluggable runtime** (Hangfire or in-memory), persisted in **SQL Server / PostgreSQL / in-memory**, and monitored via a **built-in dashboard**.

The v2.0 core invariant is **"Dispatch many, Execute once"**: idempotent dispatch via `TryRecordDispatchAsync` (dispatch ledger) + exclusive execution via `TryClaimStepAsync` (claim guard). The engine is runtime-agnostic — Hangfire is just one adapter.

### Core Concepts

- **Flow** — a workflow definition with triggers (manual, cron, webhook) and ordered steps connected via `runAfter` dependency declarations.
- **Step** — an atomic unit of work mapped by `type` name to a registered `IStepHandler`. Steps receive inputs either as static values or expressions resolved at runtime (`@triggerBody()?.orderId`, `@triggerHeaders()['X-Request-Id']`).
- **RunId** — a GUID generated per execution; everything (trigger data, step inputs/outputs, events) is stored keyed by RunId.

### Layer Breakdown

```
FlowOrchestrator.Core          Runtime-agnostic orchestration engine + all abstractions
  Abstractions/                IFlowDefinition, FlowManifest, IStepHandler, IStepInstance
  Execution/                   FlowOrchestratorEngine   ← TriggerAsync / RunStepAsync / RetryStepAsync
                               IStepDispatcher          ← bridge to runtime (Hangfire / InMemory / queue)
                               IFlowExecutor, IStepExecutor, FlowGraphPlanner
                               ForEachStepHandler, PollableStepHandler<T>
  Storage/                     IFlowStore, IFlowRunStore, IFlowRunRuntimeStore
                               IFlowRunControlStore, IOutputsRepository
  Hosting/                     FlowRunRecoveryHostedService  ← re-enqueues stuck runs on startup
  Configuration/               FlowOrchestratorBuilder, AddFlowOrchestrator() DI extension

FlowOrchestrator.Hangfire      Thin adapter — wires Hangfire as the IStepDispatcher runtime
  HangfireStepDispatcher       IStepDispatcher → IBackgroundJobClient.Enqueue/Schedule
  HangfireFlowOrchestrator     Shim: extracts JobId from PerformContext, calls FlowOrchestratorEngine
  HangfireRecurringTriggerDispatcher / Inspector  ← IRecurringJobManager adapter
  FlowSyncHostedService        On startup: syncs flows to IFlowStore, then delegates cron
                               registration to IRecurringTriggerSync (runtime-agnostic)
  RecurringTriggerSync         Hangfire impl of IRecurringTriggerSync (uses IRecurringJobManager)

FlowOrchestrator.InMemory      Pure in-process runtime + storage — no Hangfire, no database
  InMemoryStepDispatcher       Channel<T>-backed dispatcher
  InMemoryStepRunnerHostedService  BackgroundService draining the channel
  InMemoryFlowRunStore         Full IFlowRunStore + IFlowRunRuntimeStore + IFlowRunControlStore
  PeriodicTimerRecurringTriggerDispatcher
                               Cronos-backed dispatcher + inspector + sync; fires cron jobs
                               from a 1 s PeriodicTimer (parity with Hangfire RecurringJob)

FlowOrchestrator.ServiceBus    Azure Service Bus runtime adapter — cloud-native multi-replica
  ServiceBusStepDispatcher     IStepDispatcher → topic `flow-steps`; per-flow subscription
                               with SQL filter on FlowId application property; ScheduledEnqueueTime
                               for Pending/DelayNextStep
  ServiceBusFlowOrchestrator   Shim: bridges ServiceBusReceivedMessage → engine.RunStepAsync,
                               injects MessageId as JobId
  ServiceBusFlowProcessorHostedService  One ServiceBusProcessor per registered+enabled flow,
                                         drains messages, calls engine in a fresh DI scope
  ServiceBusRecurringTriggerHub  IRecurringTriggerDispatcher + Inspector + Sync in one;
                                 schedules cron messages on `flow-cron-triggers` queue
  ServiceBusCronProcessorHostedService  Drains cron queue, fires TriggerByScheduleAsync,
                                         self-perpetuates next firing as scheduled message
                                         (multi-replica safe via SB single-delivery)
  ServiceBusTopologyManager    Wraps ServiceBusAdministrationClient; idempotent
                               EnsureTopic / EnsureQueue / EnsureSubscription

FlowOrchestrator.SqlServer     Dapper-based SQL Server persistence (no EF Core)
  SqlFlowStore / SqlFlowRunStore / SqlOutputsRepository
  FlowOrchestratorSqlMigrator  Auto-creates tables on startup (FlowDefinitions, FlowRuns,
                               FlowSteps, FlowStepAttempts, FlowOutputs, FlowStepDispatches,
                               FlowStepClaims, FlowRunControls, FlowIdempotencyKeys…)

FlowOrchestrator.Dashboard     REST API + built-in HTML/JS SPA at /flows
  No Hangfire reference — uses IFlowOrchestrator, IRecurringTriggerDispatcher/Inspector
  Optional Basic Auth middleware; webhook endpoints use a separate webhookSecret
```

### Execution Flow

1. `FlowOrchestratorEngine.TriggerAsync` — **checks `IFlowStore.GetByIdAsync(flowId).IsEnabled`** (silent skip when disabled, returns `{ runId: null, disabled: true }`); checks idempotency key, generates RunId, saves trigger data, dispatches DAG entry steps via `IStepDispatcher`.
2. Runtime adapter (Hangfire job, InMemory channel consumer, or Service Bus message processor) calls `FlowOrchestratorEngine.RunStepAsync`.
3. Engine calls `TryClaimStepAsync` (claim guard) — if another worker owns the step, exits silently.
4. `DefaultStepExecutor` resolves `@triggerBody()` / `@triggerHeaders()` expressions, calls `IStepHandler.ExecuteAsync`, persists output.
5. `FlowGraphPlanner.Evaluate` returns ready steps → each dispatched via `TryRecordDispatchAsync` + `IStepDispatcher` (idempotent).
6. On `Pending`: `ReleaseDispatchAsync` then `IStepDispatcher.ScheduleStepAsync(delay)` — same step re-polled after interval.
7. On crash/restart: `FlowRunRecoveryHostedService` re-dispatches ready/waiting steps for all active runs (skips already-dispatched keys).

### Polling Pattern

`PollableStepHandler<TInput>` is a base class for steps that need to poll an external system:
- Subclass implements `FetchAsync()`.
- If condition not met, returns `Status = Pending` with `DelayNextStep` — runtime reschedules via `IStepDispatcher.ScheduleStepAsync`.
- Poll attempt counter, min-attempt validation, and timeout are managed by the base class.

### DI Registration Pattern

```csharp
// ── Hangfire runtime (production default) ──────────────────────────
builder.Services.AddHangfire(...);
builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();                 // registers HangfireStepDispatcher
    options.AddFlow<MyFlow>();
});

// ── InMemory runtime (dev / testing — no Hangfire needed) ──────────
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();                 // storage
    options.UseInMemoryRuntime();          // Channel<T> dispatcher + PeriodicTimer cron
    options.AddFlow<MyFlow>();             // no UseHangfire()
});
```

Common additions:
```csharp
builder.Services.AddStepHandler<MyHandler>("MyStepType");
builder.Services.AddFlowDashboard(builder.Configuration);  // optional

app.UseHangfireDashboard("/hangfire");   // Hangfire runtime only
app.MapFlowDashboard("/flows");
```

To swap storage, implement `IFlowStore`, `IFlowRunStore`, and `IOutputsRepository` and register directly on `options.Services` instead of calling `UseSqlServer()`.

## Dashboard UI Standards

All changes to `src/FlowOrchestrator.Dashboard/DashboardHtml.cs` (CSS, HTML, JS) **must follow `DESIGN.md`**.

### Key rules (read `DESIGN.md` for full spec)

- **Color** — use only the warm-toned palette from DESIGN.md. No cool blue-grays. Primary accent is Terracotta `#c96442`. Surface hierarchy: Parchment `#f5f4ed` → Ivory `#faf9f5` → Warm Sand `#e8e6dc`.
- **Typography** — `Inter` in this codebase maps to `Anthropic Sans` roles (UI text, labels, nav). Monospace (`JetBrains Mono`) maps to `Anthropic Mono`. No other typefaces.
- **Buttons** — follow the button styles in DESIGN.md §4 (ring-based shadows, rounded corners, warm backgrounds). Never use pure black/white flat buttons.
- **Layout** — the main-area must remain `height:100vh;overflow:hidden` so internal panels scroll, not the page. Pagination and footers must be `flex-shrink:0` siblings of scroll containers, not inside them.
- **No gradients** — depth comes from warm surface layering and ring shadows, not gradients.
- **Icons** — inline SVG only, `stroke-width:2`, consistent 16–20px sizing.

## Documentation Standards

Every new `.cs` file must have XML doc comments on all `public` and `protected` types and members.

### Required tags

```csharp
/// <summary>
/// One-sentence description of purpose. Start with a verb for methods ("Gets", "Triggers", "Returns").
/// </summary>
/// <param name="runId">The unique identifier of the flow run.</param>
/// <returns>
/// <see cref="StepResult.Succeeded"/> on success;
/// <see cref="StepResult.Failed"/> when the step cannot recover.
/// </returns>
/// <remarks>
/// Only add when behavior is non-obvious: a hidden constraint, a race condition,
/// a side effect, or a workaround for a specific limitation.
/// </remarks>
/// <exception cref="InvalidOperationException">Thrown when X is in state Y.</exception>
```

### Rules

- **Language**: English only — XML doc is part of the public API surface.
- `<summary>` is **mandatory** on every public/protected type, property, method, and constructor.
- `<param>` and `<returns>` are **mandatory** when parameters or return values are non-trivial.
- `<remarks>` only when behavior would surprise a reader (hidden constraint, side effect, non-obvious invariant).
- `<exception>` only for exceptions that callers must explicitly handle.
- **Do not** repeat the member name in the summary ("Gets the run id" for a property named `RunId` is redundant — explain the semantic instead).
- **Do not** add comments that just paraphrase the code; explain the **WHY** or the **contract**.

### Examples

```csharp
/// <summary>Unique identifier for this flow run, scoped to a single <see cref="TriggerAsync"/> invocation.</summary>
public Guid RunId { get; }

/// <summary>
/// Evaluates the DAG and returns the next step ready to execute,
/// or <see langword="null"/> if all remaining steps are blocked or complete.
/// </summary>
/// <param name="runId">The run whose graph is being evaluated.</param>
/// <param name="completedStepKey">The step that just finished, used to unlock dependents.</param>
public ValueTask<StepMetadata?> GetNextStep(Guid runId, string completedStepKey, ...);

/// <summary>Cron expression override; when set, supersedes the expression in the flow manifest.</summary>
/// <remarks>
/// Stored separately so the original manifest remains immutable.
/// Set to <see langword="null"/> to revert to the manifest-defined schedule.
/// </remarks>
public string? CronOverride { get; set; }
```

### Key Implementation Notes

- **Dapper, not EF Core** — all SQL is explicit; queries are in the `SqlServer` project.
- **`ValueTask` throughout** — minimizes allocations on the synchronous fast path.
- **`IExecutionContext`** — thread-scoped context (via `IExecutionContextAccessor`) carrying RunId, trigger data, and principal; resolved from DI during step execution.
- **Expression resolution happens at step-execution time**, not at definition time, so trigger payload is available dynamically.
- Tests use **xUnit + NSubstitute** with plain xUnit `Assert.*` and the AAA pattern (`// Arrange` / `// Act` / `// Assert`).

## Roadmap

The library has a 12-week roadmap split into 3 phases. Implementation plans
live in `.claude/plans/`. See `.claude/plans/README.md` for the full index
and status tracker.

When working on a roadmap item, read the corresponding plan file first
and follow its `Done criteria` checklist.
