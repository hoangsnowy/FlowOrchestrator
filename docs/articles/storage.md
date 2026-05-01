# Storage Backends

FlowOrchestrator's persistence layer is built on three core interfaces. The package you install provides an implementation; you can also swap in your own.

## Core Interfaces

| Interface | Responsibility |
|---|---|
| `IFlowStore` | Flow definitions and enabled/disabled state |
| `IFlowRunStore` | Run records and step status tracking |
| `IOutputsRepository` | Step input/output blobs keyed by `(RunId, StepKey)` |

These three interfaces are the **minimum required** to replace the built-in backends.

## SQL Server

```bash
dotnet add package FlowOrchestrator.SqlServer
```

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();
});
```

`FlowOrchestratorSqlMigrator` runs on startup and auto-creates all required tables if they do not exist. No manual migration step is needed.

**Tables created:**

| Table | Purpose |
|---|---|
| `FlowDefinitions` | Registered flows, enable/disable state |
| `FlowRuns` | One row per run: status, timestamps, trigger key |
| `FlowSteps` | One row per step per run: status, attempt count |
| `FlowStepAttempts` | Detailed per-attempt records (start, end, error) |
| `FlowOutputs` | Step inputs and outputs serialized as JSON |
| `FlowStepDispatches` | Idempotent dispatch ledger — supports the *Dispatch many, Execute once* invariant |
| `FlowStepClaims` | Exclusive execution claim per step (the *Execute once* half) |
| `FlowRunControls` | Cancellation, timeout, and idempotency key records |
| `FlowIdempotencyKeys` | Trigger-time idempotency dedupe |
| `FlowEvents` | Event stream records (when `EnableEventPersistence = true`) |
| `FlowSignalWaiters` | Parked `WaitForSignal` step state (see [WaitForSignal](wait-for-signal.md)) |
| `FlowScheduleStates` | Cron override storage (when `Scheduler.PersistOverrides = true`) |

**Connection string format:**

```json
{
  "ConnectionStrings": {
    "FlowOrchestrator": "Server=.;Database=FlowOrchestrator;Trusted_Connection=True;"
  }
}
```

---

## PostgreSQL

```bash
dotnet add package FlowOrchestrator.PostgreSQL
```

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UsePostgreSql(connectionString);
    options.UseHangfire();
});
```

`FlowOrchestratorPgMigrator` creates the same table set in PostgreSQL on startup. Uses `Npgsql` — no EF Core dependency.

```json
{
  "ConnectionStrings": {
    "FlowOrchestratorPg": "Host=localhost;Database=floworch;Username=app;Password=secret"
  }
}
```

---

## In-Memory

```bash
dotnet add package FlowOrchestrator.InMemory
```

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();
    options.UseHangfire();
});
```

> [!WARNING]
> All run data is lost when the process restarts. Use this for local development and unit tests only.

`UseInMemory()` must be called **explicitly** — there is no silent fallback. Calling `AddFlowOrchestrator()` without any storage backend throws `InvalidOperationException` on startup.

---

## Comparing Backends

| Feature | SQL Server | PostgreSQL | In-Memory |
|---|---|---|---|
| Persistence across restarts | Yes | Yes | No |
| Run history and step timeline | Yes | Yes | Yes (current session) |
| Schedule override persistence | Yes | Yes | No |
| Cron expressions | Yes | Yes | Yes |
| Webhook deduplication | Yes | Yes | Session only |
| Testcontainers support | Yes | Yes | — |
| Production-ready | Yes | Yes | No |

---

## Custom Backend

Implement the three core interfaces:

```csharp
public sealed class RedisFlowStore : IFlowStore { ... }
public sealed class RedisFlowRunStore : IFlowRunStore { ... }
public sealed class RedisOutputsRepository : IOutputsRepository { ... }
```

Register them directly on `options.Services`:

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.Services.AddSingleton<IFlowStore, RedisFlowStore>();
    options.Services.AddSingleton<IFlowRunStore, RedisFlowRunStore>();
    options.Services.AddSingleton<IOutputsRepository, RedisOutputsRepository>();
    options.UseHangfire();
});
```

### Advanced Contracts

For full feature parity (schedule overrides, run control, event stream, retention, and concurrency safety), implement these additional interfaces:

| Interface | Feature |
|---|---|
| `IFlowScheduleStateStore` | Persistent cron overrides (`Scheduler.PersistOverrides`) |
| `IFlowRunControlStore` | Cancel, timeout, and idempotency key state. The engine checks this on every `TriggerAsync` to deduplicate runs and on every `RunStepAsync` to honour cancellation. |
| `IFlowEventReader` | Run event stream (`GET /flows/api/runs/{runId}/events`) |
| `IFlowRetentionStore` | Background retention sweep (deletes old run data) |
| `IFlowSignalStore` | Parked `WaitForSignal` waiter state. Required if you want to use the [`WaitForSignal`](wait-for-signal.md) built-in step on a custom backend. |
| `IFlowRunRuntimeStore` | Step claim/dispatch ledger per run. Implements `TryRecordDispatchAsync` (idempotent INSERT — prevents duplicate dispatch) and `TryClaimStepAsync` (claim exclusion — ensures a step is executed by at most one worker). Required for production use with any multi-worker runtime. |

Register these the same way — directly on `options.Services`.

---

## Hangfire Storage vs FlowOrchestrator Storage

These are **completely independent** stores:

- **Hangfire storage** — holds job queue, server heartbeats, retry state. Configured on `AddHangfire(...)`.
- **FlowOrchestrator storage** — holds flow definitions, run history, step outputs. Configured on `options.UseSqlServer(...)` / `UsePostgreSql()` / `UseInMemory()`.

You can mix them freely:

```csharp
// Hangfire on SQL Server, FlowOrchestrator on PostgreSQL
builder.Services.AddHangfire(c => c.UseSqlServerStorage(sqlConnStr));
builder.Services.AddFlowOrchestrator(options =>
{
    options.UsePostgreSql(pgConnStr);
    options.UseHangfire();
});
```
