## FlowOrchestrator

**[📖 Documentation](https://hoangsnowy.github.io/FlowOrchestrator/)** · **[NuGet](https://www.nuget.org/packages/FlowOrchestrator.Core)** · **[GitHub](https://github.com/hoangsnowy/FlowOrchestrator)**

FlowOrchestrator is an open-source .NET library for orchestrating **multi-step background workflows** — defined as code-first C# manifests, executed by **Hangfire**, persisted in **SQL Server**, **PostgreSQL**, or **in-memory**, and monitored via a **built-in dashboard**.

**Features at a glance:**
- Define flows as plain C# classes — no YAML, no JSON files to maintain
- Three trigger types: Manual (dashboard/API), Cron (recurring schedule), Webhook (external HTTP POST)
- DAG execution planner — supports fan-out/fan-in with `runAfter` dependency conditions
- `@triggerBody()` / `@triggerHeaders()` expressions — bind trigger payload fields to step inputs at runtime
- `PollableStepHandler<T>` — built-in retry-with-backoff for steps that wait on external systems
- `ForEach` loop steps — iterate over collections and fan out parallel/sequential child steps
- Full run history — step-by-step timeline, input/output capture, attempt tracking
- Retry button — re-enqueue any failed step from the dashboard without restarting the whole run
- Run control — cooperative cancel + timeout support per run
- Idempotent triggers via `Idempotency-Key` header
- Scheduler durability — pause/cron override state persists across restarts
- Run event stream + OpenTelemetry metrics/traces
- Built-in retention cleanup (optional)
- Optional Basic Auth on the dashboard; webhook secret validation via `X-Webhook-Key`

---

## Compatibility

| Package | Target frameworks |
|---|---|
| `FlowOrchestrator.Core` | `net8.0` · `net9.0` · `net10.0` |
| `FlowOrchestrator.Hangfire` | `net8.0` · `net9.0` · `net10.0` |
| `FlowOrchestrator.InMemory` | `net8.0` · `net9.0` · `net10.0` |
| `FlowOrchestrator.SqlServer` | `net8.0` · `net9.0` · `net10.0` |
| `FlowOrchestrator.PostgreSQL` | `net8.0` · `net9.0` · `net10.0` |
| `FlowOrchestrator.Dashboard` | `net8.0` · `net9.0` · `net10.0` |

---

## 1. Install

```bash
dotnet add package FlowOrchestrator.Core
dotnet add package FlowOrchestrator.Hangfire
dotnet add package FlowOrchestrator.Dashboard   # optional — REST API + SPA dashboard

# Pick exactly one storage backend:
dotnet add package FlowOrchestrator.SqlServer    # SQL Server (Dapper, no EF Core)
dotnet add package FlowOrchestrator.PostgreSQL   # PostgreSQL (Dapper + Npgsql)
dotnet add package FlowOrchestrator.InMemory     # In-process, no database (testing / dev)
```

> **Required:** A storage backend must always be registered explicitly.
> Calling `AddFlowOrchestrator()` without calling `UseSqlServer()`, `UsePostgreSql()`, or `UseInMemory()` throws `InvalidOperationException` at startup.

Hangfire must also be installed separately:

```bash
dotnet add package Hangfire.Core
dotnet add package Hangfire.AspNetCore

# Match your FlowOrchestrator storage choice:
dotnet add package Hangfire.SqlServer   # SQL Server backend
dotnet add package Hangfire.InMemory    # PostgreSQL or InMemory backends
```

---

## 2. Quick Start

### SQL Server

```csharp
// Program.cs
builder.Services.AddHangfire(c => c
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));
builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);   // SQL persistence + auto-migration
    options.UseHangfire();                    // Hangfire job bridge
    options.AddFlow<OrderFulfillmentFlow>();  // Register your flow(s)
});

builder.Services.AddStepHandler<FetchOrdersStep>("FetchOrders");
builder.Services.AddStepHandler<SubmitToWmsStep>("SubmitToWms");

builder.Services.AddFlowDashboard(builder.Configuration); // optional

app.UseHangfireDashboard("/hangfire");
app.MapFlowDashboard("/flows");
```

### PostgreSQL

```csharp
builder.Services.AddHangfire(c => c.UseInMemoryStorage()); // Hangfire.InMemory
builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UsePostgreSql(connectionString);  // PostgreSQL persistence + auto-migration
    options.UseHangfire();
    options.AddFlow<MyFlow>();
});
```

### InMemory (development / testing)

```csharp
builder.Services.AddHangfire(c => c.UseInMemoryStorage()); // Hangfire.InMemory
builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();   // ← must be called explicitly; no implicit fallback
    options.UseHangfire();
    options.AddFlow<MyFlow>();
});
```

### Advanced runtime options (optional)

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();
    options.AddFlow<MyFlow>();

    // Run control defaults
    options.RunControl.IdempotencyHeaderName = "Idempotency-Key";
    options.RunControl.DefaultRunTimeout = TimeSpan.FromMinutes(30); // null = disabled

    // Scheduler state
    options.Scheduler.PersistOverrides = true;

    // Observability
    options.Observability.EnableEventPersistence = true;
    options.Observability.EnableOpenTelemetry = true;

    // Retention cleanup
    options.Retention.Enabled = true;
    options.Retention.DataTtl = TimeSpan.FromDays(30);
    options.Retention.SweepInterval = TimeSpan.FromHours(1);
});
```

---

## 3. Flow Definition

```csharp
// OrderFulfillmentFlow.cs
public sealed class OrderFulfillmentFlow : IFlowDefinition
{
    // ⚠ Always use a fixed GUID literal — never Guid.NewGuid().
    // Guid.NewGuid() would create a new database row on every restart.
    public Guid Id { get; } = new Guid("a1b2c3d4-0000-0000-0000-000000000002");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"]  = new TriggerMetadata { Type = TriggerType.Manual },
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?>
                {
                    ["webhookSlug"]   = "order-fulfillment",
                    ["webhookSecret"] = "your-secret-key"  // validated via X-Webhook-Key header
                }
            }
        },
        Steps = new StepCollection
        {
            ["fetch_orders"] = new StepMetadata
            {
                Type = "FetchOrders",
                Inputs = new Dictionary<string, object?> { ["status"] = "Pending" }
            },
            ["submit_to_wms"] = new StepMetadata
            {
                Type = "SubmitToWms",
                RunAfter = new RunAfterCollection { ["fetch_orders"] = [StepStatus.Succeeded] }
            }
        }
    };
}
```

---

## 4. Core Concepts

### Flow

A **Flow** is a named, versioned workflow definition with **Triggers** and **Steps**. Flows are C# classes implementing `IFlowDefinition`. They are registered at startup via `options.AddFlow<T>()`, synced to the database (so the dashboard can discover them), and survive app restarts without creating duplicate records — as long as the `Id` GUID is fixed.

### Step

Each step has:
- `Type` — a logical string name mapped to a registered `IStepHandler`
- `RunAfter` — declares which predecessors must reach which statuses before this step executes
- `Inputs` — static values or [trigger expressions](#7-expression-reference)

Steps execute as Hangfire background jobs — one job per step. If a step fails the run stops there. The dashboard **Retry** button re-enqueues the failed step without restarting the whole run.

### RunId

Each trigger generates a new `RunId` (GUID). All trigger data, step inputs, outputs, and events are stored keyed by `RunId`.

### Trigger types

| Type | How to start | Required inputs |
|------|-------------|-----------------|
| `TriggerType.Manual` | Dashboard button or `POST /flows/api/flows/{id}/trigger` | — |
| `TriggerType.Cron` | Hangfire recurring job | `cronExpression` (e.g. `"0 2 * * *"`) |
| `TriggerType.Webhook` | `POST /flows/api/webhook/{slug}` | `webhookSlug`; optionally `webhookSecret` |

### Step statuses

| Status | Meaning |
|--------|---------|
| `Pending` | Waiting or polling |
| `Running` | Hangfire job is executing |
| `Succeeded` | Handler returned successfully |
| `Failed` | Handler threw or returned a failed result |
| `Skipped` | Prerequisite did not reach the required status |

### Run statuses

Common run statuses include `Running`, `Succeeded`, `Failed`, plus control-plane statuses `Cancelled` (cooperative cancel requested) and `TimedOut` (timeout threshold reached).

---

## 5. Step Handlers

### 5.1 Plain handler (`IStepHandler<TInput>`)

```csharp
public sealed class ValidateOrderInput
{
    public string OrderId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
}

public sealed class ValidateOrderHandler : IStepHandler<ValidateOrderInput>
{
    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance<ValidateOrderInput> step)
    {
        var orderId = step.Inputs.OrderId;
        // ... do work ...
        return new { valid = true, orderId };
    }
}
```

Register in DI:
```csharp
builder.Services.AddStepHandler<ValidateOrderHandler>("ValidateOrder");
```

Reference from a flow manifest:
```csharp
["validate_order"] = new StepMetadata
{
    Type = "ValidateOrder",
    Inputs = new Dictionary<string, object?>
    {
        ["orderId"]   = "@triggerBody()?.orderId",
        ["requestId"] = "@triggerHeaders()['X-Request-Id']"
    }
}
```

### 5.2 Accessing the current run from DI

Inject `IExecutionContextAccessor` to read `RunId` and outputs from anywhere in the DI graph:

```csharp
public sealed class MyHandler : IStepHandler<MyInput>
{
    private readonly IExecutionContextAccessor _ctx;
    private readonly IOutputsRepository _outputs;

    public MyHandler(IExecutionContextAccessor ctx, IOutputsRepository outputs)
    {
        _ctx = ctx;
        _outputs = outputs;
    }

    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext context, IFlowDefinition flow, IStepInstance<MyInput> step)
    {
        var runId = _ctx.Context!.RunId;
        var prev = await _outputs.GetStepOutputAsync(runId, "previous_step_key");
        // ...
    }
}
```

### 5.3 Pollable handler (`PollableStepHandler<TInput>`)

For steps that wait on an external system (batch jobs, payment confirmation, carrier tracking), extend `PollableStepHandler<TInput>`:

```csharp
public sealed class CheckJobStatusInput : IPollableInput
{
    public string JobId { get; set; } = string.Empty;

    // Polling configuration — set via step Inputs in the manifest
    public bool PollEnabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 10;
    public int PollTimeoutSeconds { get; set; } = 300;
    public int PollMinAttempts { get; set; } = 1;
    public string? PollConditionPath { get; set; }    // dot-notation JSON path
    public object? PollConditionEquals { get; set; }  // succeed when path == this value

    // Internal poll state (do not rename — persisted between attempts)
    [JsonPropertyName("__pollStartedAtUtc")]
    public string? PollStartedAtUtc { get; set; }
    [JsonPropertyName("__pollAttempt")]
    public int? PollAttempt { get; set; }
}

public sealed class CheckJobStatusHandler : PollableStepHandler<CheckJobStatusInput>
{
    private readonly IHttpClientFactory _http;
    public CheckJobStatusHandler(IHttpClientFactory http) => _http = http;

    protected override async ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance<CheckJobStatusInput> step)
    {
        var json = await _http.CreateClient("ExternalApi")
                              .GetStringAsync($"/jobs/{step.Inputs.JobId}");
        return (JsonDocument.Parse(json).RootElement, true);
    }
}
```

Configure polling via the manifest:
```csharp
["check_job_status"] = new StepMetadata
{
    Type = "CheckJobStatus",
    Inputs = new Dictionary<string, object?>
    {
        ["jobId"]               = "@triggerBody()?.jobId",
        ["pollEnabled"]         = true,
        ["pollIntervalSeconds"] = 10,
        ["pollTimeoutSeconds"]  = 300,
        ["pollMinAttempts"]     = 1,
        ["pollConditionPath"]   = "status",
        ["pollConditionEquals"] = "completed"
    }
}
```

`PollableStepHandler` automatically:
- Tracks `PollAttempt` and `PollStartedAtUtc` in the persisted step inputs
- Returns `StepStatus.Pending` + `DelayNextStep` when the condition is not yet met — Hangfire re-schedules after the delay
- Returns `StepStatus.Failed` when `PollTimeoutSeconds` is exceeded
- Evaluates `PollConditionPath` (dot-notation) against the JSON response

### 5.4 ForEach loop steps

Use `LoopStepMetadata` to iterate over a collection and fan out child steps for each item:

```csharp
["process_each_order"] = new LoopStepMetadata
{
    Type = "ForEach",
    ForEach = "@triggerBody()?.orders",  // expression resolving to an array
    ConcurrencyLimit = 3,                // max parallel child executions (0 = sequential)
    Steps = new StepCollection
    {
        ["validate_item"] = new StepMetadata { Type = "ValidateOrder" }
    }
}
```

Each iteration is enqueued as a separate Hangfire job. `step.Index` is available inside the child handler.

---

## 6. Trigger Examples

**Manual only:**
```csharp
["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
```

**Cron (every day at 2 AM UTC):**
```csharp
["nightly"] = new TriggerMetadata
{
    Type = TriggerType.Cron,
    Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 2 * * *" }
}
```

**Webhook with secret:**
```csharp
["payment_gateway"] = new TriggerMetadata
{
    Type = TriggerType.Webhook,
    Inputs = new Dictionary<string, object?>
    {
        ["webhookSlug"]   = "payment-event",
        ["webhookSecret"] = "your-secret-key"  // client sends X-Webhook-Key: your-secret-key
    }
}
```

**Step dependencies (`runAfter`):**

```csharp
// Run only when validate_order succeeded
["process_payment"] = new StepMetadata
{
    Type = "ProcessPayment",
    RunAfter = new RunAfterCollection { ["validate_order"] = [StepStatus.Succeeded] }
}

// Run on success OR failure of process_payment (e.g. a cleanup or notification step)
["send_notification"] = new StepMetadata
{
    Type = "SendNotification",
    RunAfter = new RunAfterCollection
    {
        ["process_payment"] = [StepStatus.Succeeded, StepStatus.Failed]
    }
}
```

Steps with no `RunAfter` are the flow's initial steps (executed immediately after trigger).

---

## 7. Expression Reference

Step `Inputs` values starting with `@` are evaluated at runtime against the current trigger data.

| Expression | Returns |
|---|---|
| `@triggerBody()` | Full trigger payload (`JsonElement`) |
| `@triggerBody()?.field` | Top-level field (null-safe) |
| `@triggerBody()?.nested.child` | Nested field via dot notation |
| `@triggerBody()?.items[0].name` | Array element access |
| `@triggerHeaders()` | All captured request headers as a dictionary |
| `@triggerHeaders()['X-Request-Id']` | Specific header (case-insensitive lookup) |

Non-matching strings are passed through as-is.

**Headers never captured:** `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `X-Webhook-Key`, `Connection`, `Transfer-Encoding`, `Upgrade`, `Content-Length`.

---

## 8. Running the Sample App

The sample app (`samples/FlowOrchestrator.SampleApp`) demonstrates an e-commerce **OrderHub** scenario with four flows:

| Flow | Triggers | Demonstrates |
|---|---|---|
| `HelloWorldFlow` | Manual, Cron every minute | Minimal sequential steps |
| `OrderFulfillmentFlow` | Manual, Webhook `/order-fulfillment` | DB query → polling API → save result |
| `ShipmentTrackingFlow` | Manual | Polling pattern (Pending → Pending → Succeeded) |
| `PaymentEventFlow` | Webhook `/payment-event` | `@triggerBody()` expression extraction |

### Option A — Via .NET Aspire (recommended, requires Docker Desktop)

```powershell
dotnet run --project .\FlowOrchestrator.AppHost\FlowOrchestrator.AppHost.csproj
```

Aspire spins up **SQL Server 2022** and **PostgreSQL 16** containers and launches the sample app **three times in parallel** — once per storage backend — each on a dedicated port:

| Aspire resource | Backend | Hangfire storage | Flows available |
|---|---|---|---|
| `flow-sqlserver` | SQL Server | `Hangfire.SqlServer` | Hello, Order, Shipment, Payment |
| `flow-postgresql` | PostgreSQL | `Hangfire.InMemory` | Hello, Shipment, Payment |
| `flow-inmemory` | InMemory | `Hangfire.InMemory` | Hello, Shipment, Payment |

Open the **Aspire dashboard** at `http://localhost:18888` to see all three resources and their URLs. Each instance exposes `/flows` and `/hangfire` independently — trigger the same flow across different backends and compare run histories side-by-side.

> `OrderFulfillmentFlow` requires the `Orders` business table and is only registered on the SQL Server instance.

### Option B — Local (no Docker, using LocalDB)

Create `samples/FlowOrchestrator.SampleApp/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "FlowOrchestrator": "Server=(localdb)\\mssqllocaldb;Database=FlowOrchestrator;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "FlowDashboard": {
    "Branding": {
      "Title": "OrderHub",
      "Subtitle": "Powered by FlowOrchestrator"
    },
    "BasicAuth": {
      "Username": "admin",
      "Password": "change-me",
      "Realm": "OrderHub Dashboard"
    }
  }
}
```

`BasicAuth` is optional — omit `Username`/`Password` to disable dashboard auth.

```powershell
dotnet run --project .\samples\FlowOrchestrator.SampleApp\FlowOrchestrator.SampleApp.csproj
```

The app defaults to `http://localhost:5201`. Open `http://localhost:5201/flows` for the dashboard.

---

## 9. Dashboard Guide

Open `http://localhost:5201/flows` (or your configured base path).

### Pages

**Overview** — Stats cards (registered flows, active runs, completed/failed today, scheduled jobs).

**Flows** — Catalog of all registered flows. Click any flow to see:
- Manifest details (ID, name, version, enabled/disabled)
- Steps table (key, type, inputs, `runAfter` dependencies)
- Triggers table with webhook URL and **Copy** button
- **Schedule** tab: cron jobs with next/last execution, inline cron expression editor, pause/resume, and trigger-now controls
- DAG graph (step dependency graph as SVG)
- Raw JSON manifest viewer
- **Enable/Disable** toggle and **Trigger** button

**Runs** — Filterable run list (by flow, status, or free-text search). Search matches run fields (`id`, `flowName`, `status`, `triggerKey`) and step trace fields (`stepKey`, `errorMessage`, `outputJson`). Click a run to see the step-by-step timeline with timing, inputs, outputs, and errors. Failed steps show a **Retry** button. You can also request cooperative **Cancel**, inspect run **Control** state (timeout/cancel/idempotency), and query run **Events**.

**Scheduled** — Hangfire recurring jobs: flow name, trigger key, cron expression, next/last execution. Actions: trigger immediately, pause, resume, edit cron expression inline.

### Triggering flows for testing

**From the dashboard:**
1. Go to **Flows** → click a flow card → click **Trigger** (optionally paste a JSON body).
2. Switch to **Runs** to monitor execution.

**Via the REST API:**
```http
POST http://localhost:5201/flows/api/flows/a1b2c3d4-0000-0000-0000-000000000002/trigger
Content-Type: application/json
Idempotency-Key: order-123-manual-001

{ "note": "manual test run" }
```

**Via webhook (PaymentEventFlow):**
```http
POST http://localhost:5201/flows/api/webhook/payment-event
Content-Type: application/json

{
  "payload": { "id": "pay_abc123", "orderId": "ord_456", "amount": 99.99, "status": "confirmed" },
  "event": "payment.confirmed",
  "timestamp": "2026-04-16T10:00:00Z"
}
```

**Via webhook with secret (OrderFulfillmentFlow):**
```http
POST http://localhost:5201/flows/api/webhook/order-fulfillment
Content-Type: application/json
X-Webhook-Key: your-secret-key

{}
```

---

## 10. REST API Reference

All endpoints are under the base path configured in `MapFlowDashboard(basePath)` (default: `/flows`).

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/flows/api/flows` | List all registered flow definitions |
| `GET` | `/flows/api/flows/{id}` | Get flow definition (with manifest) |
| `POST` | `/flows/api/flows/{id}/enable` | Enable flow (restores cron recurring jobs) |
| `POST` | `/flows/api/flows/{id}/disable` | Disable flow (removes cron recurring jobs) |
| `POST` | `/flows/api/flows/{id}/trigger` | Manually trigger a flow run |
| `POST` | `/flows/api/webhook/{idOrSlug}` | Webhook endpoint: trigger by flow ID or slug |
| `GET` | `/flows/api/handlers` | List registered step handler type names |
| `GET` | `/flows/api/runs` | List runs (`?flowId=`, `?status=`, `?search=`, `?skip=`, `?take=`, `?includeTotal=`) |
| `GET` | `/flows/api/runs/active` | List currently running flows |
| `GET` | `/flows/api/runs/stats` | Dashboard statistics |
| `GET` | `/flows/api/runs/{id}` | Run detail with trigger headers |
| `GET` | `/flows/api/runs/{id}/steps` | Step details for a run |
| `GET` | `/flows/api/runs/{runId}/events` | Run event stream (`?skip=`, `?take=`) |
| `GET` | `/flows/api/runs/{runId}/control` | Run control state (timeout/cancel/idempotency) |
| `POST` | `/flows/api/runs/{runId}/cancel` | Request cooperative run cancellation |
| `POST` | `/flows/api/runs/{runId}/steps/{stepKey}/retry` | Retry a failed step |
| `GET` | `/flows/api/schedules` | List recurring jobs with status |
| `POST` | `/flows/api/schedules/{jobId}/trigger` | Trigger a recurring job immediately |
| `POST` | `/flows/api/schedules/{jobId}/pause` | Pause a recurring job |
| `POST` | `/flows/api/schedules/{jobId}/resume` | Resume a paused recurring job |
| `PUT` | `/flows/api/schedules/{jobId}/cron` | Update cron expression (`{ "cronExpression": "..." }`) |
| `GET` | `/hangfire` | Hangfire job queue dashboard |

---

## 11. Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                       Your ASP.NET Core app                      │
│  AddFlowOrchestrator()   AddStepHandler<T>()   MapFlowDashboard  │
└──────┬──────────────────────────┬──────────────────────┬─────────┘
       │                          │                      │
┌──────▼──────┐     ┌─────────────▼────────┐  ┌─────────▼────────┐
│ .Hangfire   │     │  Storage backend     │  │  .Dashboard      │
│             │     │  (pick one)          │  │                  │
│ Hangfire    │     │  .SqlServer          │  │  REST API        │
│ FlowOrch.   │     │    SqlFlowStore      │  │  SPA at /flows   │
│ DefaultStep │     │    SqlFlowRunStore   │  │  Basic Auth      │
│ Executor    │     │    SqlOutputsRepo    │  │  Webhook endpt   │
│ ForEach     │     │    SqlMigrator       │  └──────────────────┘
│ Handler     │     │  .PostgreSQL         │
│ FlowSync    │     │    PgFlowStore       │
│ Hosted      │     │    PgFlowRunStore    │
│ Service     │     │    PgOutputsRepo     │
│ Recurring   │     │    PgMigrator        │
│ TriggerSync │     │  .InMemory           │
└──────┬──────┘     │    InMemoryFlowStore │
       │ Hangfire   │    InMemoryFlowRun   │
┌──────▼──────┐     │    Store             │
│  Hangfire   │     │    InMemoryOutputs   │
│  Job Queue  │     │    Repository        │
└─────────────┘     └─────────────┬────────┘
                                  │ IFlowStore
                                  │ IFlowRunStore
                                  │ IOutputsRepository
                    ┌─────────────▼────────┐
                    │      .Core           │
                    │                      │
                    │  IFlowDefinition     │
                    │  IStepHandler<T>     │
                    │  PollableStep        │
                    │  Handler<T>          │
                    │  IFlowExecutor       │
                    │  IFlowStore          │
                    │  IFlowRunStore       │
                    │  IOutputsRepository  │
                    └──────────────────────┘
```

### Layer overview

**`FlowOrchestrator.Core`** — Framework-agnostic abstractions and execution engine.
- `IFlowDefinition`, `FlowManifest`, `StepCollection`, `TriggerMetadata` — flow DSL
- `IFlowExecutor` — step ordering: determines which step to run next given `runAfter` conditions
- `IStepExecutor` / `IStepHandler<T>` — step execution and output capture
- `PollableStepHandler<T>` — base class for polling-based steps
- `IFlowStore` / `IFlowRunStore` / `IOutputsRepository` — storage abstractions

**`FlowOrchestrator.Hangfire`** — Hangfire integration layer.
- `HangfireFlowOrchestrator` — `TriggerAsync`, `RunStepAsync`, `RetryStepAsync`
- `FlowGraphPlanner` — DAG planning (fan-out/fan-in), readiness/blocked evaluation
- `DefaultStepExecutor` — resolves `@triggerBody()` / `@triggerHeaders()` expressions, dispatches to `IStepHandler`
- `ForEachStepHandler` — built-in handler for `LoopStepMetadata`
- `FlowSyncHostedService` — on startup: syncs flows to `IFlowStore`, registers Hangfire recurring jobs for Cron triggers
- `RecurringTriggerSync` — keeps recurring jobs in sync when flows are enabled/disabled at runtime
- Run control + idempotency + retention hosted services
- **Fail-fast validation** — throws `InvalidOperationException` at startup if no storage backend is registered

**`FlowOrchestrator.InMemory`** — Pure in-process storage backend (no database required).
- `InMemoryFlowStore`, `InMemoryFlowRunStore`, `InMemoryOutputsRepository`
- `UseInMemory(this FlowOrchestratorBuilder)` DI extension
- All data is lost when the process restarts — use for development or testing only

**`FlowOrchestrator.SqlServer`** — Dapper-based SQL Server persistence.
- `SqlFlowStore`, `SqlFlowRunStore`, `SqlOutputsRepository`
- `FlowOrchestratorSqlMigrator` — `IHostedService` that auto-creates tables on startup:
  `FlowDefinitions`, `FlowRuns`, `FlowSteps`, `FlowStepAttempts`, `FlowOutputs`, `FlowStepClaims`, `FlowRunControls`, `FlowIdempotencyKeys`, `FlowEvents`, `FlowScheduleStates`
- `UseSqlServer(this FlowOrchestratorBuilder, string connectionString)` DI extension

**`FlowOrchestrator.PostgreSQL`** — Dapper-based PostgreSQL persistence (Npgsql).
- `PgFlowStore`, `PgFlowRunStore`, `PgOutputsRepository`
- `FlowOrchestratorPgMigrator` — auto-creates tables on startup
- `UsePostgreSql(this FlowOrchestratorBuilder, string connectionString)` DI extension

**`FlowOrchestrator.Dashboard`** — Built-in monitoring UI and REST API.
- Minimal API endpoints for flow catalog, triggering, run history, retry, schedules
- Embedded single-page application (HTML/JS/CSS) served at the configured base path
- Optional Basic Auth middleware; webhook endpoint validates `X-Webhook-Key`

### Execution flow (step-by-step)

1. A trigger fires (dashboard button, cron schedule, or webhook POST).
2. `HangfireFlowOrchestrator.TriggerAsync` generates a `RunId`, applies idempotency/timeout configuration, saves trigger data and headers, computes DAG entry steps, and enqueues all runnable entries.
3. Hangfire fires `RunStepAsync` → `DefaultStepExecutor` resolves input expressions, dispatches to the matching `IStepHandler`, and persists the output.
4. `FlowGraphPlanner` evaluates run state: enqueue all newly ready steps, mark permanently blocked steps as `Skipped`, and prevent duplicate enqueue with step-claim idempotency.
5. Run completes as `Succeeded`/`Failed`/`Cancelled`/`TimedOut` based on final step outcomes and run-control state.
6. For polling steps → if `FetchAsync` returns a result where the condition is not yet met, `PollableStepHandler` returns `Pending` + `DelayNextStep`. Hangfire schedules the re-run after the configured interval.

### Swapping storage

```csharp
// SQL Server
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();
    options.AddFlow<MyFlow>();
});

// PostgreSQL
builder.Services.AddFlowOrchestrator(options =>
{
    options.UsePostgreSql(connectionString);
    options.UseHangfire();
    options.AddFlow<MyFlow>();
});

// InMemory — must be called explicitly; there is no implicit fallback
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();
    options.UseHangfire();
    options.AddFlow<MyFlow>();
});
```

To plug in a fully custom backend, implement `IFlowStore`, `IFlowRunStore`, and `IOutputsRepository` and register them on `options.Services`:

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseHangfire();
    options.AddFlow<MyFlow>();
    options.Services.AddSingleton<IFlowStore, MyRedisFlowStore>();
    options.Services.AddSingleton<IFlowRunStore, MyRedisFlowRunStore>();
    options.Services.AddSingleton<IOutputsRepository, MyRedisOutputsRepository>();
});
```

Optional advanced contracts for full vNext behavior:
- `IFlowRunRuntimeStore` (step claim/status graph orchestration)
- `IFlowRunControlStore` (cancel/timeout/idempotency state)
- `IFlowScheduleStateStore` (persistent pause/cron overrides)
- `IFlowEventReader` (run event query API)
- `IFlowRetentionStore` (retention cleanup hook)

---

## 12. Solution Layout

```
src/
  FlowOrchestrator.Core          Core abstractions and execution engine
  FlowOrchestrator.Hangfire      Hangfire bridge, DI extensions, built-in step handlers
  FlowOrchestrator.InMemory      In-process storage backend (dev / testing)
  FlowOrchestrator.SqlServer     Dapper/SQL Server persistence + auto-migrator
  FlowOrchestrator.PostgreSQL    Dapper/PostgreSQL persistence + auto-migrator (Npgsql)
  FlowOrchestrator.Dashboard     REST API + embedded HTML/JS dashboard SPA

samples/
  FlowOrchestrator.SampleApp     Runnable ASP.NET Core demo (OrderHub scenario)
                                 Selects backend via FLOW_STORAGE env var:
                                   "sqlserver" | "postgresql" | "inmemory" (default)

FlowOrchestrator.AppHost/        .NET Aspire host — launches 3 SampleApp instances
                                 (SQL Server · PostgreSQL · InMemory) side-by-side

tests/
  FlowOrchestrator.Core.Tests          Unit tests for Core abstractions and execution engine
  FlowOrchestrator.InMemory.Tests      Unit tests for InMemory storage backend
  FlowOrchestrator.Hangfire.Tests      Unit tests for DI registration and Hangfire bridge
  FlowOrchestrator.Dashboard.Tests     Integration tests via ASP.NET Core TestHost
  FlowOrchestrator.SqlServer.Tests     Integration tests via Testcontainers (requires Docker)
  FlowOrchestrator.PostgreSQL.Tests    Integration tests via Testcontainers (requires Docker)
```

### Running tests

```bash
# All unit tests (no Docker required)
dotnet test tests/FlowOrchestrator.Core.Tests/
dotnet test tests/FlowOrchestrator.InMemory.Tests/
dotnet test tests/FlowOrchestrator.Hangfire.Tests/
dotnet test tests/FlowOrchestrator.Dashboard.Tests/

# Integration tests (requires Docker Desktop)
dotnet test tests/FlowOrchestrator.SqlServer.Tests/
dotnet test tests/FlowOrchestrator.PostgreSQL.Tests/

# Everything at once
dotnet test
```

---

## 13. License

MIT — see the `LICENSE` file.
