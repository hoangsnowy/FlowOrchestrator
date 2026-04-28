# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Workflow Rules

- **Always execute immediately** — never stop at planning. Make file changes first, then describe what was done. If asked to implement something, write the code now, don't outline steps.
- **Always confirm completion explicitly** — after finishing work, state: what files were changed, build status, and test results.
- **Always write tests** — any bug fix or new feature must include unit tests. Do not wait to be asked. Run them and confirm they pass.
- **Always verify the build** — run `dotnet build` after changes. If errors exist, fix them and rebuild. Do not report completion until the build is clean.
- **Always add XML doc comments** — every new file must have `///` XML doc comments on all public types and members. Follow the Documentation Standards section below.

## Testing

- Tests live in `./tests/`. Framework: xUnit + FluentAssertions + NSubstitute.
- After any code change: run `dotnet test` and confirm the test count and pass rate.
- New tests go in the matching test project (e.g., changes in `FlowOrchestrator.Core` → tests in `FlowOrchestrator.Core.Tests`).

## Build & Verification

- Before reporting a task complete: `dotnet build` must show 0 errors, 0 warnings (or document why warnings are acceptable).
- If build fails after a fix, keep iterating — do not stop and ask unless truly stuck after 3+ attempts.

## Commands

```bash
# Build
dotnet build
dotnet build --configuration Release

# Run all tests
dotnet test

# Run a single test project
dotnet test ./tests/FlowOrchestrator.Core.Tests/FlowOrchestrator.Core.Tests.csproj
dotnet test ./tests/FlowOrchestrator.Hangfire.Tests/FlowOrchestrator.Hangfire.Tests.csproj

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
  FlowSyncHostedService        On startup: syncs flows to IFlowStore, registers cron recurring jobs
  RecurringTriggerSync         Keeps recurring jobs in sync when flows are enabled/disabled

FlowOrchestrator.InMemory      Pure in-process runtime — no Hangfire, no database
  InMemoryStepDispatcher       Channel<T>-backed dispatcher
  InMemoryStepRunnerHostedService  BackgroundService draining the channel
  InMemoryFlowRunStore         Full IFlowRunStore + IFlowRunRuntimeStore + IFlowRunControlStore
  NullRecurring*               No-op IRecurringTriggerDispatcher / Inspector / Sync

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

1. `FlowOrchestratorEngine.TriggerAsync` — checks idempotency key, generates RunId, saves trigger data, dispatches DAG entry steps via `IStepDispatcher`.
2. Runtime adapter (Hangfire job or InMemory channel consumer) calls `FlowOrchestratorEngine.RunStepAsync`.
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
builder.Services.AddInMemoryRuntime();     // ← call BEFORE AddFlowOrchestrator
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();                 // storage
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
- Tests use **xUnit + FluentAssertions + NSubstitute**.
