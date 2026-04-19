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

FlowOrchestrator is a .NET library for orchestrating workflows from **declarative JSON/code manifests**, executed via **Hangfire**, with **SQL Server persistence** and a **built-in dashboard**.

### Core Concepts

- **Flow** — a workflow definition with triggers (manual, cron, webhook) and ordered steps connected via `runAfter` dependency declarations.
- **Step** — an atomic unit of work mapped by `type` name to a registered `IStepHandler`. Steps receive inputs either as static values or expressions resolved at runtime (`@triggerBody()?.orderId`, `@triggerHeaders()['X-Request-Id']`).
- **RunId** — a GUID generated per execution; everything (trigger data, step inputs/outputs, events) is stored keyed by RunId.

### Layer Breakdown

```
FlowOrchestrator.Core          Core abstractions and execution engine
  Abstractions/                IFlowDefinition, FlowManifest, IStepHandler, IStepInstance
  Execution/                   IFlowExecutor (step ordering), IStepExecutor (run handler)
  Storage/                     IFlowStore, IFlowRunStore, IOutputsRepository + in-memory impls
  Configuration/               FlowOrchestratorBuilder, AddFlowOrchestrator() DI extension

FlowOrchestrator.Hangfire      Hangfire integration — job enqueueing and coordination
  HangfireFlowOrchestrator     TriggerAsync / RunStepAsync / RetryStepAsync entry points
  FlowSyncHostedService        On startup: syncs code-defined flows to IFlowStore, registers cron recurring jobs
  DefaultStepExecutor          Resolves IStepHandler by name, evaluates trigger expressions for inputs
  ForEachStepHandler           Built-in loop handler (parallel/sequential item processing)
  RecurringTriggerSync         Manages Hangfire RecurringJob lifecycle for cron triggers

FlowOrchestrator.SqlServer     Dapper-based SQL Server persistence (no EF Core)
  SqlFlowStore / SqlFlowRunStore / SqlOutputsRepository
  FlowOrchestratorSqlMigrator  Auto-creates tables on startup (FlowDefinitions, FlowRuns, FlowSteps, FlowStepAttempts, FlowOutputs)

FlowOrchestrator.Dashboard     REST API + built-in HTML/JS SPA at /flows
  Endpoints for flow catalog, trigger, retry, run history, DAG graph visualization
  Optional Basic Auth middleware; webhook endpoints use a separate webhookSecret
```

### Execution Flow

1. `TriggerAsync` → generates RunId, saves trigger data, calls `IFlowExecutor.TriggerFlow()` to get first step, enqueues it as a Hangfire job.
2. Hangfire fires `RunStepAsync` → `DefaultStepExecutor` resolves input expressions, calls `IStepHandler.ExecuteAsync`, persists output.
3. `IFlowExecutor.GetNextStep()` evaluates `runAfter` conditions; if satisfied, enqueues next step; otherwise marks run complete/failed.
4. On failure: dashboard exposes **Retry** button → `RetryStepAsync` resets step state and re-enqueues from that point.

### Polling Pattern

`PollableStepHandler<TInput>` is a base class for steps that need to poll an external system:
- Subclass implements `FetchAsync()`.
- If condition not met, returns `Status = Pending` with `DelayNextStep` — Hangfire reschedules.
- Poll attempt counter, min-attempt validation, and timeout are managed by the base class.

### DI Registration Pattern

```csharp
// Hangfire must be registered separately before FlowOrchestrator
builder.Services.AddHangfire(...);
builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);  // or omit for in-memory
    options.UseHangfire();
    options.AddFlow<MyFlow>();
});

builder.Services.AddStepHandler<MyHandler>("MyStepType");

builder.Services.AddFlowDashboard(builder.Configuration);  // optional

app.UseHangfireDashboard("/hangfire");
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
