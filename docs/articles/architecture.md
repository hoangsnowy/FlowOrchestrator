# Architecture

FlowOrchestrator is a runtime-agnostic workflow engine. The core execution logic lives in `FlowOrchestrator.Core` and is completely independent of any background-job framework. Hangfire is one of several runtime adapters — the engine talks to runtimes through `IStepDispatcher`, not directly to Hangfire APIs.

## Layer Diagram

```
┌─────────────────────────────────────────────────────────┐
│  Your Application Code                                   │
│  IFlowDefinition  ·  IStepHandler<T>                    │
└────────────────────────┬────────────────────────────────┘
                         │ AddFlowOrchestrator()
┌────────────────────────▼────────────────────────────────┐
│  FlowOrchestrator.Core   (engine layer)                  │
│                                                          │
│  FlowOrchestratorEngine — TriggerAsync / RunStepAsync    │
│  DefaultStepExecutor    — input resolution + dispatch    │
│  FlowGraphPlanner       — DAG evaluation                 │
│  FlowSyncHostedService  — startup sync + cron wiring     │
│  FlowRunRecoveryHostedService — re-dispatch on startup   │
│  ForEachStepHandler     — built-in loop execution        │
└──────┬────────────────────────────────┬─────────────────┘
       │ IStepDispatcher                │ IFlowStore / IFlowRunStore
┌──────▼──────────────────────────┐  ┌──▼────────────────────────────────┐
│  Runtime Adapter (choose one)   │  │  Storage Backend (choose one)      │
│  FlowOrchestrator.Hangfire      │  │  FlowOrchestrator.SqlServer        │
│    HangfireStepDispatcher       │  │  FlowOrchestrator.PostgreSQL       │
│  FlowOrchestrator.InMemory      │  │  FlowOrchestrator.InMemory         │
│    InMemoryStepDispatcher       │  └───────────────────────────────────┘
│  FlowOrchestrator.ServiceBus    │
│    ServiceBusStepDispatcher     │
└─────────────────────────────────┘
┌────────────────────────────────────────────────────────────────┐
│  FlowOrchestrator.Dashboard                                     │
│  REST API (/flows/api/**)  ·  SPA at /flows                     │
└────────────────────────────────────────────────────────────────┘
```

## Package Responsibilities

| Package | Responsibility |
|---|---|
| `FlowOrchestrator.Core` | Engine, abstractions, DAG planning, `FlowOrchestratorEngine`, `IStepDispatcher`, `DefaultStepExecutor`, `PollableStepHandler<T>`, in-memory storage |
| `FlowOrchestrator.Hangfire` | Hangfire adapter: `HangfireStepDispatcher`, `RecurringTriggerSync`, cron job management |
| `FlowOrchestrator.InMemory` | Channel-based in-process runtime + storage: `InMemoryStepDispatcher`, `InMemoryStepRunnerHostedService`, `PeriodicTimerRecurringTriggerDispatcher` (Cronos cron parser), full `InMemoryFlowStore` / `InMemoryFlowRunStore` / `InMemoryOutputsRepository` |
| `FlowOrchestrator.ServiceBus` | Azure Service Bus adapter (v1.22+): `ServiceBusStepDispatcher` (topic + per-flow subscription), `ServiceBusFlowProcessorHostedService` (one processor per enabled flow), `ServiceBusRecurringTriggerHub` + `ServiceBusCronProcessorHostedService` (self-perpetuating scheduled cron messages), `ServiceBusTopologyManager` (admin-client topology auto-create) |
| `FlowOrchestrator.SqlServer` | Dapper + SQL Server persistence, auto-migration of 9 tables |
| `FlowOrchestrator.PostgreSQL` | Dapper + Npgsql PostgreSQL persistence, auto-migration |
| `FlowOrchestrator.Dashboard` | REST API endpoints + embedded SPA (HTML/JS/CSS) served at a configurable base path |

## Execution Flow

The sequence from trigger to completion:

1. **Trigger** — A call to `FlowOrchestratorEngine.TriggerAsync()` first consults `IFlowStore.GetByIdAsync(flowId).IsEnabled`; when `false`, the call silent-skips and returns `{ runId: null, disabled: true }` without dispatching (EventId 1010 `TriggerRejectedDisabledFlow` warning). Otherwise it checks the idempotency key, generates a `RunId`, persists trigger headers/body, and calls `IFlowExecutor.TriggerFlow()` to find the first ready steps. Each entry step is dispatched via `IStepDispatcher.EnqueueStepAsync()`, guarded by `TryRecordDispatchAsync` to prevent duplicate dispatch.

2. **Claim** — The runtime adapter (Hangfire job, InMemory channel consumer, or Service Bus message processor) calls `FlowOrchestratorEngine.RunStepAsync`. The engine calls `TryClaimStepAsync` first — if another worker has already claimed this step, the current call exits silently (the "Execute once" half of the **Dispatch many, Execute once** invariant).

3. **Dispatch** — `DefaultStepExecutor` resolves `@triggerBody()` / `@triggerHeaders()` expressions against the persisted trigger data, then calls `IStepHandler.ExecuteAsync`.

4. **Execute** — The handler runs business logic and returns an output object (or a `StepResult<T>` to control status explicitly).

5. **Persist output** — The output is serialized and stored in `IOutputsRepository`. Step status is updated in `IFlowRunStore`.

6. **Advance** — `FlowGraphPlanner.Evaluate` evaluates `runAfter` conditions. If one or more steps are now unblocked, they are dispatched via `IStepDispatcher`. If a step returned `StepStatus.Pending`, the engine calls `ReleaseDispatchAsync` then `IStepDispatcher.ScheduleStepAsync(delay)` to reschedule. If all steps are complete, the run is marked `Succeeded` or `Failed`.

7. **On failure** — The dashboard exposes a **Retry** button that calls `FlowOrchestratorEngine.RetryStepAsync()`, which resets the step to `Pending` and re-dispatches it from the failure point. Preceding outputs are preserved.

## Startup Sequence

`FlowSyncHostedService` runs on `IHostedService.StartAsync`:

1. Calls `IFlowStore.UpsertAsync` for every registered `IFlowDefinition` — creates or updates the flow record in the database.
2. Delegates cron-trigger registration to `IRecurringTriggerSync.SyncTriggers(flowId, isEnabled)` — runtime-agnostic. The Hangfire impl writes to `IRecurringJobManager`; the InMemory impl writes to an in-process `PeriodicTimer` registry. Both apply persisted cron overrides from `IFlowScheduleStateStore` when `Scheduler.PersistOverrides = true` and remove jobs for disabled flows.

`FlowRunRecoveryHostedService` also runs on startup. It re-dispatches any steps that were in a ready state when the previous process terminated — preventing stuck runs after a restart.

This means the database always reflects the code — no manual migration step required when you add or rename a flow.

## Dispatch Many, Execute Once

This is the core concurrency invariant:

- **`TryRecordDispatchAsync`** — an idempotent dispatch ledger (INSERT once per `RunId + StepKey`). Multiple workers may attempt to enqueue the same step (e.g., when two predecessors complete nearly simultaneously), but only the first INSERT succeeds.
- **`TryClaimStepAsync`** — claim exclusion within a run. When `RunStepAsync` is called, the engine acquires a claim. If another worker already claimed the step, the call exits without executing the handler.

These two guards together ensure a step's handler is called exactly once even under concurrent dispatch.

## Storage Separation

FlowOrchestrator's storage and (when using the Hangfire adapter) Hangfire's storage are **independent**. A common production setup uses SQL Server for both, but you can mix them:

```csharp
// Hangfire on SQL Server, FlowOrchestrator on PostgreSQL
builder.Services.AddHangfire(c => c.UseSqlServerStorage(hangfireSqlConnStr));
builder.Services.AddFlowOrchestrator(options =>
{
    options.UsePostgreSql(pgConnStr);
    options.UseHangfire();
});
```

The `IFlowStore` / `IFlowRunStore` / `IOutputsRepository` interfaces are the only contracts FlowOrchestrator depends on for core storage. Implementing these three interfaces is all that is required to swap in a custom backend (Redis, DynamoDB, CosmosDB, etc.).

## Key Design Decisions

**Runtime-agnostic engine** — `FlowOrchestratorEngine` in `FlowOrchestrator.Core` owns all orchestration logic. The `IStepDispatcher` abstraction decouples it from any specific background-job framework. Adding a new runtime adapter requires only an `IStepDispatcher` implementation.

**Dapper, not EF Core** — all SQL is explicit. No ORM magic, no shadow queries. Queries live in the `SqlServer` / `PostgreSQL` projects and are readable as raw SQL.

**`ValueTask` throughout** — minimises allocations on the synchronous fast-path. Step handlers that return synchronously avoid a `Task` allocation entirely.

**Expression resolution at execution time** — `@triggerBody()?.orderId` is resolved when `RunStepAsync` fires, not when the manifest is parsed. This means the trigger payload is always available regardless of when steps run or are retried.

**No hidden fallbacks** — calling `AddFlowOrchestrator()` without `UseSqlServer()`, `UsePostgreSql()`, or `UseInMemory()` throws an `InvalidOperationException` on startup. Silent defaults lead to hard-to-diagnose production issues.
