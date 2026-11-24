# Architecture

FlowOrchestrator is a runtime-agnostic workflow engine. The core execution logic lives in `FlowOrchestrator.Core` and is completely independent of any background-job framework. Hangfire is one of several runtime adapters — the engine talks to runtimes through `IStepDispatcher`, not directly to Hangfire APIs.

## Layer Diagram

```
┌─────────────────────────────────────────────────────────┐
│  Your Application Code                                   │
│  IFlowDefinition  ·  IStepHandler<T>                    │
└────────────────────────┬────────────────────────────────┘
                         │ AddFlowOrchestrator()  — ships in FlowOrchestrator.Core   
┌────────────────────────▼────────────────────────────────┐
│  FlowOrchestrator.Core   (engine layer)                  │
│                                                          │
│  FlowOrchestratorEngine — TriggerAsync / RunStepAsync    │
│  DefaultStepExecutor    — input resolution + dispatch    │
│  FlowGraphPlanner       — DAG evaluation                 │
│  FlowTimeoutEnforcementHostedService — timeout sweep     │
│  FlowRunRecoveryHostedService — re-dispatch on startup   │
│  FlowSyncHostedService  — flow upsert + cron sync        │
│  RecurringTriggerSync   — effective cron, every runtime  │
│  FlowRegistry           — O(1) flow lookup by id         │
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
| `FlowOrchestrator.Core` | Engine, abstractions, DAG planning, `FlowOrchestratorEngine`, `IStepDispatcher`, `DefaultStepExecutor`, `PollableStepHandler<T>`, storage *interfaces* (`IFlowStore` / `IFlowRunStore` / `IOutputsRepository`), the `AddFlowOrchestrator()` DI entry point, `FlowRegistry`, `FlowSyncHostedService` (startup flow validate + upsert + cron wiring), `RecurringTriggerSync` (effective cron for every runtime), retention sweep, in-process `InMemoryFlowScheduleStateStore` default. No Hangfire and no Newtonsoft.Json dependency. |
| `FlowOrchestrator.Hangfire` | Hangfire runtime adapter, selected with `options.UseHangfire()`: `HangfireStepDispatcher` (slim `HangfireStepEnvelope` job argument), `HangfireRecurringTriggerDispatcher` / `Inspector` (Hangfire recurring jobs), `HangfireFlowOrchestrator` job target, W3C trace-context job filter |
| `FlowOrchestrator.InMemory` | Channel-based in-process runtime + storage: `InMemoryStepDispatcher`, `InMemoryStepRunnerHostedService`, `PeriodicTimerRecurringTriggerDispatcher` (Cronos cron parser), full `InMemoryFlowStore` / `InMemoryFlowRunStore` / `InMemoryOutputsRepository` |
| `FlowOrchestrator.ServiceBus` | Azure Service Bus adapter (v1.22+): `ServiceBusStepDispatcher` (topic + per-flow subscription), `ServiceBusFlowProcessorHostedService` (one processor per enabled flow), `ServiceBusRecurringTriggerHub` + `ServiceBusCronProcessorHostedService` (self-perpetuating scheduled cron messages), `ServiceBusTopologyManager` (admin-client topology auto-create) |
| `FlowOrchestrator.SqlServer` | Dapper + SQL Server persistence, auto-migration of the full schema on startup (flow definitions, runs, steps, attempts, outputs, claims, dispatches, run controls, idempotency keys, events, signal waiters, schedule states, webhook replay nonces and rejections) |
| `FlowOrchestrator.PostgreSQL` | Dapper + Npgsql PostgreSQL persistence, auto-migration |
| `FlowOrchestrator.Dashboard` | REST API endpoints + embedded SPA (HTML/JS/CSS) served at a configurable base path |

> [!NOTE]
> Since 2.0 `AddFlowOrchestrator()` ships in `FlowOrchestrator.Core`. Only Hangfire hosts reference `FlowOrchestrator.Hangfire`; the SQL Server, PostgreSQL, InMemory, Service Bus, Dashboard and Testing packages carry no Hangfire or Newtonsoft.Json dependency.

## Execution Flow

The sequence from trigger to completion:

1. **Trigger** — A call to `FlowOrchestratorEngine.TriggerAsync()` first consults `IFlowStore.GetByIdAsync(flowId).IsEnabled`; when `false`, the call silent-skips and returns `new FlowTriggerResult(RunId: null, Disabled: true)` without dispatching (the HTTP layer surfaces that as `409`/`403`, not as a silent `200`) (EventId 1010 `TriggerRejectedDisabledFlow` warning). Otherwise it checks the idempotency key, generates a `RunId`, persists trigger headers/body, and calls `IFlowGraphPlanner.CreateEntrySteps()` to build every entry-step instance. Each entry step is dispatched via `IStepDispatcher.EnqueueStepAsync()`, guarded by `TryRecordDispatchAsync` to prevent duplicate dispatch.

2. **Claim** — The runtime adapter (Hangfire job, InMemory channel consumer, or Service Bus message processor) calls `FlowOrchestratorEngine.RunStepAsync`. The engine calls `TryClaimStepAsync` first — if another worker has already claimed this step, the current call exits silently (the "Execute once" half of the **Dispatch many, Execute once** invariant).

3. **Dispatch** — `DefaultStepExecutor` resolves `@triggerBody()` / `@triggerHeaders()` expressions against the persisted trigger data, then calls `IStepHandler.ExecuteAsync`.

4. **Execute** — The handler runs business logic and returns an output object (or a `StepResult<T>` to control status explicitly).

5. **Persist output** — The output is serialized and stored in `IOutputsRepository`. Step status is updated in `IFlowRunStore`.

6. **Advance** — `FlowGraphPlanner.Evaluate` evaluates `runAfter` conditions. If one or more steps are now unblocked, they are dispatched via `IStepDispatcher`. If a step returned `StepStatus.Pending`, the engine calls `ReleaseDispatchAsync` then `IStepDispatcher.ScheduleStepAsync(delay)` to reschedule. If all steps are complete, the run is marked `Succeeded` or `Failed`.

7. **On failure** — The dashboard exposes a **Retry** button that calls `FlowOrchestratorEngine.RetryStepAsync()`. Only a `Failed` step can be retried: the store resets it with a compare-and-swap (so of two concurrent retries one wins and the other gets `409`), and the engine re-dispatches it through `IStepDispatcher` from the failure point; the handler runs on the runtime, not in the request. Preceding outputs are preserved.

## Startup Sequence

`FlowSyncHostedService` runs on `IHostedService.StartAsync`:

1. Validates each registered `IFlowDefinition` via `IFlowGraphPlanner.Validate` — an invalid manifest throws `InvalidOperationException` and fails startup — then calls `IFlowStore.SaveAsync` to upsert the flow record in the database.
2. Synchronises cron triggers through `IRecurringTriggerSync.SyncTriggersAsync(flowId, isEnabled)`. Core's single `RecurringTriggerSync` resolves the effective cron — removed for disabled flows and dashboard-paused schedules, the persisted override from `IFlowScheduleStateStore` when `Scheduler.PersistOverrides = true`, else the manifest cron — and hands it to the runtime's `IRecurringTriggerDispatcher` (Hangfire `IRecurringJobManager`, the in-process `PeriodicTimer` registry, or Service Bus scheduled messages). Job ids come from `RecurringTriggerJobId` (`flow-{flowId}-{triggerKey}`).

`FlowRunRecoveryHostedService` also runs on startup. It re-dispatches any steps that were in a ready state when the previous process terminated — preventing stuck runs after a restart. Recovery is a scheduling path, so it applies the same rules as the graph continuation before dispatching: a ready step whose `When` clause is false is recorded `Skipped` (with its evaluation trace) instead of running, a step whose prerequisites finished in an unaccepted status is skipped, and a dispatch that throws is logged without abandoning the run's other steps.

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

Swapping in a custom backend (Redis, DynamoDB, CosmosDB, etc.) requires five contracts at minimum: `IFlowStore`, `IFlowRunStore`, `IFlowRunRuntimeStore` (DAG evaluation and the execution claim guard), `IFlowRunControlStore` (idempotency keys, run deadlines, cancellation), and `IOutputsRepository` (the code-defined flow registry, `FlowRegistry`, is built by Core from your `AddFlow<T>()` registrations). `AddFlowOrchestrator()` throws an `InvalidOperationException` when `IFlowStore` or a runtime (`IStepDispatcher` + `IRecurringTriggerDispatcher`) is missing, and `FlowOrchestratorEngine` throws `InvalidOperationException` on construction when `IFlowRunRuntimeStore` or `IFlowRunControlStore` is missing (the signal dispatcher and the recovery service likewise require `IFlowRunRuntimeStore`) — there is no reduced-functionality fallback. For full functionality also implement `IFlowRetentionStore`, `IFlowSignalStore`, and `IFlowScheduleStateStore` (Core registers the process-local `InMemoryFlowScheduleStateStore` when absent).

## Key Design Decisions

**Runtime-agnostic engine** — `FlowOrchestratorEngine` in `FlowOrchestrator.Core` owns all orchestration logic. The `IStepDispatcher` abstraction decouples it from any specific background-job framework. Adding a new runtime adapter requires only an `IStepDispatcher` implementation. A runtime whose queue does not survive a process restart (the in-memory channel) reports `IStepDispatcher.LosesQueuedStepsOnRestart = true`; startup recovery then re-enqueues ledgered steps that never started instead of trusting the ledger row to mean "still queued". The default (`false`) suits any runtime backed by durable storage; a dispatcher decorator must forward it.

**Dapper, not EF Core** — all SQL is explicit. No ORM magic, no shadow queries. Queries live in the `SqlServer` / `PostgreSQL` projects and are readable as raw SQL.

**`ValueTask` throughout** — minimises allocations on the synchronous fast-path. Step handlers that return synchronously avoid a `Task` allocation entirely.

**Expression resolution at execution time** — `@triggerBody()?.orderId` is resolved when `RunStepAsync` fires, not when the manifest is parsed. This means the trigger payload is always available regardless of when steps run or are retried.

**No hidden fallbacks** — calling `AddFlowOrchestrator()` without `UseSqlServer()`, `UsePostgreSql()`, or `UseInMemory()` throws an `InvalidOperationException` on startup. Silent defaults lead to hard-to-diagnose production issues.
