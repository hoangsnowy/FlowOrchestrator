# Architecture

FlowOrchestrator is a runtime-agnostic workflow engine. The core execution logic lives in `FlowOrchestrator.Core` and is completely independent of any background-job framework. Hangfire is one of several runtime adapters — the engine talks to runtimes through `IStepDispatcher`, not directly to Hangfire APIs.

## Layer Diagram

```
┌─────────────────────────────────────────────────────────┐
│  Your Application Code                                   │
│  IFlowDefinition  ·  IStepHandler<T>                    │
└────────────────────────┬────────────────────────────────┘
                         │ AddFlowOrchestrator()  — ships in FlowOrchestrator.Hangfire
┌────────────────────────▼────────────────────────────────┐
│  FlowOrchestrator.Core   (engine layer)                  │
│                                                          │
│  FlowOrchestratorEngine — TriggerAsync / RunStepAsync    │
│  DefaultStepExecutor    — input resolution + dispatch    │
│  FlowGraphPlanner       — DAG evaluation                 │
│  FlowTimeoutEnforcementHostedService — timeout sweep     │
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
| `FlowOrchestrator.Core` | Engine, abstractions, DAG planning, `FlowOrchestratorEngine`, `IStepDispatcher`, `DefaultStepExecutor`, `PollableStepHandler<T>`, storage *interfaces* (`IFlowStore` / `IFlowRunStore` / `IOutputsRepository`) — no storage implementations |
| `FlowOrchestrator.Hangfire` | Hangfire adapter: `HangfireStepDispatcher`, `RecurringTriggerSync`, cron job management, `FlowSyncHostedService` (startup flow validate + upsert + cron wiring) |
| `FlowOrchestrator.InMemory` | Channel-based in-process runtime + storage: `InMemoryStepDispatcher`, `InMemoryStepRunnerHostedService`, `PeriodicTimerRecurringTriggerDispatcher` (Cronos cron parser), full `InMemoryFlowStore` / `InMemoryFlowRunStore` / `InMemoryOutputsRepository` |
| `FlowOrchestrator.ServiceBus` | Azure Service Bus adapter (v1.22+): `ServiceBusStepDispatcher` (topic + per-flow subscription), `ServiceBusFlowProcessorHostedService` (one processor per enabled flow), `ServiceBusRecurringTriggerHub` + `ServiceBusCronProcessorHostedService` (self-perpetuating scheduled cron messages), `ServiceBusTopologyManager` (admin-client topology auto-create) |
| `FlowOrchestrator.SqlServer` | Dapper + SQL Server persistence, auto-migration of the full schema on startup (flow definitions, runs, steps, attempts, outputs, claims, dispatches, run controls, idempotency keys, events, signal waiters, schedule states, webhook replay nonces and rejections) |
| `FlowOrchestrator.PostgreSQL` | Dapper + Npgsql PostgreSQL persistence, auto-migration |
| `FlowOrchestrator.Dashboard` | REST API endpoints + embedded SPA (HTML/JS/CSS) served at a configurable base path |

> [!NOTE]
> `AddFlowOrchestrator()` currently ships in the `FlowOrchestrator.Hangfire` package, so that package must be referenced for DI bootstrap regardless of which runtime adapter you select. Selecting `UseInMemoryRuntime()` or the Service Bus adapter still replaces the Hangfire `IStepDispatcher` — no Hangfire server is started unless you call `AddHangfireServer()` yourself.

## Execution Flow

The sequence from trigger to completion:

1. **Trigger** — A call to `FlowOrchestratorEngine.TriggerAsync()` first consults `IFlowStore.GetByIdAsync(flowId).IsEnabled`; when `false`, the call silent-skips and returns `{ runId: null, disabled: true }` without dispatching (EventId 1010 `TriggerRejectedDisabledFlow` warning). Otherwise it checks the idempotency key, generates a `RunId`, persists trigger headers/body, and calls `IFlowGraphPlanner.CreateEntrySteps()` to build every entry-step instance. Each entry step is dispatched via `IStepDispatcher.EnqueueStepAsync()`, guarded by `TryRecordDispatchAsync` to prevent duplicate dispatch.

2. **Claim** — The runtime adapter (Hangfire job, InMemory channel consumer, or Service Bus message processor) calls `FlowOrchestratorEngine.RunStepAsync`. The engine calls `TryClaimStepAsync` first — if another worker has already claimed this step, the current call exits silently (the "Execute once" half of the **Dispatch many, Execute once** invariant).

3. **Dispatch** — `DefaultStepExecutor` resolves `@triggerBody()` / `@triggerHeaders()` expressions against the persisted trigger data, then calls `IStepHandler.ExecuteAsync`.

4. **Execute** — The handler runs business logic and returns an output object (or a `StepResult<T>` to control status explicitly).

5. **Persist output** — The output is serialized and stored in `IOutputsRepository`. Step status is updated in `IFlowRunStore`.

6. **Advance** — `FlowGraphPlanner.Evaluate` evaluates `runAfter` conditions. If one or more steps are now unblocked, they are dispatched via `IStepDispatcher`. If a step returned `StepStatus.Pending`, the engine calls `ReleaseDispatchAsync` then `IStepDispatcher.ScheduleStepAsync(delay)` to reschedule. If all steps are complete, the run is marked `Succeeded` or `Failed`.

7. **On failure** — The dashboard exposes a **Retry** button that calls `FlowOrchestratorEngine.RetryStepAsync()`, which resets the step to `Pending` and re-dispatches it from the failure point. Preceding outputs are preserved.

## Startup Sequence

`FlowSyncHostedService` runs on `IHostedService.StartAsync`:

1. Validates each registered `IFlowDefinition` via `IFlowGraphPlanner.Validate` — an invalid manifest throws `InvalidOperationException` and fails startup — then calls `IFlowStore.SaveAsync` to upsert the flow record in the database.
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

Swapping in a custom backend (Redis, DynamoDB, CosmosDB, etc.) requires four contracts at minimum: `IFlowStore`, `IFlowRunStore`, `IOutputsRepository`, and `IFlowRepository` — `AddFlowOrchestrator()` throws an `InvalidOperationException` on startup when either `IFlowStore` or `IFlowRepository` is missing. For full functionality also implement `IFlowRunRuntimeStore` (without it the claim guard is disabled and the engine falls back to legacy sequential mode, EventId 9000), `IFlowRunControlStore`, `IFlowRetentionStore`, `IFlowEventReader`, `IFlowSignalStore`, and `IFlowScheduleStateStore` (an ephemeral in-process default is registered when absent).

## Key Design Decisions

**Runtime-agnostic engine** — `FlowOrchestratorEngine` in `FlowOrchestrator.Core` owns all orchestration logic. The `IStepDispatcher` abstraction decouples it from any specific background-job framework. Adding a new runtime adapter requires only an `IStepDispatcher` implementation.

**Dapper, not EF Core** — all SQL is explicit. No ORM magic, no shadow queries. Queries live in the `SqlServer` / `PostgreSQL` projects and are readable as raw SQL.

**`ValueTask` throughout** — minimises allocations on the synchronous fast-path. Step handlers that return synchronously avoid a `Task` allocation entirely.

**Expression resolution at execution time** — `@triggerBody()?.orderId` is resolved when `RunStepAsync` fires, not when the manifest is parsed. This means the trigger payload is always available regardless of when steps run or are retried.

**No hidden fallbacks** — calling `AddFlowOrchestrator()` without `UseSqlServer()`, `UsePostgreSql()`, or `UseInMemory()` throws an `InvalidOperationException` on startup. Silent defaults lead to hard-to-diagnose production issues.
