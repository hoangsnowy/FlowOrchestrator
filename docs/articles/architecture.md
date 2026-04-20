# Architecture

FlowOrchestrator is built as a thin orchestration layer on top of Hangfire. It adds declarative flow definitions, DAG execution planning, expression resolution, and a monitoring dashboard — while delegating all job scheduling and retry logic to Hangfire.

## Layer Diagram

```
┌─────────────────────────────────────────────────────────┐
│  Your Application Code                                   │
│  IFlowDefinition  ·  IStepHandler<T>                    │
└────────────────────────┬────────────────────────────────┘
                         │ AddFlowOrchestrator()
┌────────────────────────▼────────────────────────────────┐
│  FlowOrchestrator.Hangfire   (coordination layer)        │
│                                                          │
│  HangfireFlowOrchestrator — TriggerAsync / RunStepAsync  │
│  DefaultStepExecutor       — input resolution + dispatch │
│  FlowGraphPlanner          — DAG evaluation              │
│  FlowSyncHostedService     — startup sync + cron wiring  │
│  ForEachStepHandler        — built-in loop execution     │
└──────┬────────────────────────────────┬─────────────────┘
       │ IBackgroundJobClient           │ IFlowStore / IFlowRunStore
┌──────▼──────────┐          ┌──────────▼───────────────────────┐
│    Hangfire     │          │  Storage Backend (choose one)     │
│  (job queue +   │          │  FlowOrchestrator.SqlServer       │
│   persistence)  │          │  FlowOrchestrator.PostgreSQL      │
└─────────────────┘          │  FlowOrchestrator.InMemory        │
                             └──────────────────────────────────┘
┌────────────────────────────────────────────────────────────────┐
│  FlowOrchestrator.Dashboard                                     │
│  REST API (/flows/api/**)  ·  SPA at /flows                     │
└────────────────────────────────────────────────────────────────┘
```

## Package Responsibilities

| Package | Responsibility |
|---|---|
| `FlowOrchestrator.Core` | Framework-agnostic abstractions: `IFlowDefinition`, `IStepHandler<T>`, `FlowManifest`, `PollableStepHandler<T>`, execution context |
| `FlowOrchestrator.Hangfire` | Hangfire bridge: job enqueuing, step execution, DAG planning, cron sync, DI extensions |
| `FlowOrchestrator.SqlServer` | Dapper + SQL Server persistence, auto-migration of 9 tables |
| `FlowOrchestrator.PostgreSQL` | Dapper + Npgsql PostgreSQL persistence, auto-migration |
| `FlowOrchestrator.InMemory` | Thread-safe in-process storage — all data lost on restart |
| `FlowOrchestrator.Dashboard` | REST API endpoints + embedded SPA (HTML/JS/CSS) served at a configurable base path |

## Execution Flow

The sequence from trigger to completion:

1. **Trigger** — A call to `HangfireFlowOrchestrator.TriggerAsync()` generates a `RunId`, persists trigger headers/body, and calls `IFlowExecutor.TriggerFlow()` to find the first ready steps.

2. **Enqueue** — Each ready step is enqueued as a Hangfire background job referencing `HangfireFlowOrchestrator.RunStepAsync(runId, stepKey)`.

3. **Dispatch** — Hangfire fires `RunStepAsync`. `DefaultStepExecutor` resolves `@triggerBody()` / `@triggerHeaders()` expressions against the persisted trigger data, then calls `IStepHandler.ExecuteAsync`.

4. **Execute** — The handler runs business logic and returns an output object (or a `StepResult<T>` to control status explicitly).

5. **Persist output** — The output is serialized and stored in `IOutputsRepository`. Step status is updated in `IFlowRunStore`.

6. **Advance** — `IFlowExecutor.GetNextStep()` evaluates `runAfter` conditions. If one or more steps are now unblocked, they are enqueued as new Hangfire jobs. If all steps are complete, the run is marked `Succeeded` or `Failed`.

7. **On failure** — The dashboard exposes a **Retry** button that calls `HangfireFlowOrchestrator.RetryStepAsync()`, which resets the step to `Pending` and re-enqueues it from the failure point. Preceding outputs are preserved.

## Startup Sequence

`FlowSyncHostedService` runs on `IHostedService.StartAsync`:

1. Calls `IFlowStore.UpsertAsync` for every registered `IFlowDefinition` — creates or updates the flow record in the database.
2. Iterates cron triggers: registers `RecurringJob` entries in Hangfire for each, applying any persisted cron overrides from `IFlowScheduleStateStore` when `Scheduler.PersistOverrides = true`.
3. Removes stale recurring jobs for flows that have been unregistered or disabled.

This means the database always reflects the code — no manual migration step required when you add or rename a flow.

## Storage Separation

FlowOrchestrator's storage and Hangfire's storage are **independent**. A common production setup uses SQL Server for both, but you can mix them:

```csharp
// Hangfire on SQL Server, FlowOrchestrator on PostgreSQL
builder.Services.AddHangfire(c => c.UseSqlServerStorage(hangfireSqlConnStr));
builder.Services.AddFlowOrchestrator(options =>
{
    options.UsePostgreSql(pgConnStr);
    options.UseHangfire();
});
```

The `IFlowStore` / `IFlowRunStore` / `IOutputsRepository` interfaces are the only contracts FlowOrchestrator depends on. Implementing these three interfaces is all that is required to swap in a custom backend (Redis, DynamoDB, CosmosDB, etc.).

## Key Design Decisions

**Dapper, not EF Core** — all SQL is explicit. No ORM magic, no shadow queries. Queries live in the `SqlServer` / `PostgreSQL` projects and are readable as raw SQL.

**`ValueTask` throughout** — minimises allocations on the synchronous fast-path. Step handlers that return synchronously avoid a `Task` allocation entirely.

**Expression resolution at execution time** — `@triggerBody()?.orderId` is resolved when `RunStepAsync` fires, not when the manifest is parsed. This means the trigger payload is always available regardless of when steps run or are retried.

**No hidden fallbacks** — calling `AddFlowOrchestrator()` without `UseSqlServer()`, `UsePostgreSql()`, or `UseInMemory()` throws an `InvalidOperationException` on startup. Silent defaults lead to hard-to-diagnose production issues.
