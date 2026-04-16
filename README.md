## FlowOrchestrator

FlowOrchestrator is an open-source .NET library for orchestrating **multi-step background workflows** — defined as code-first C# manifests, executed by **Hangfire**, persisted in **SQL Server**, and monitored via a **built-in dashboard**.

**Features at a glance:**
- Define flows as plain C# classes — no YAML, no JSON files to maintain
- Three trigger types: Manual (dashboard/API), Cron (recurring schedule), Webhook (external HTTP POST)
- `runAfter` dependency graph — steps execute when their predecessors reach specific statuses
- `@triggerBody()` / `@triggerHeaders()` expressions — bind trigger payload fields to step inputs at runtime
- `PollableStepHandler<T>` — built-in retry-with-backoff for steps that wait on external systems
- `ForEach` loop steps — iterate over collections and fan out parallel/sequential child steps
- Full SQL run history — step-by-step timeline, input/output capture, attempt tracking
- Retry button — re-enqueue any failed step from the dashboard without restarting the whole run
- Optional Basic Auth on the dashboard; webhook secret validation via `X-Webhook-Key`

---

## Compatibility

NuGet packages (`FlowOrchestrator.Core`, `.Hangfire`, `.SqlServer`, `.Dashboard`) target: **`net8.0`**, **`net9.0`**, **`net10.0`**.

---

## 1. Install

```bash
dotnet add package FlowOrchestrator.Core
dotnet add package FlowOrchestrator.Hangfire
dotnet add package FlowOrchestrator.SqlServer
dotnet add package FlowOrchestrator.Dashboard
```

Hangfire itself must also be installed:
```bash
dotnet add package Hangfire.Core
dotnet add package Hangfire.SqlServer
dotnet add package Hangfire.AspNetCore
```

---

## 2. Quick Start

```csharp
// Program.cs — minimal wiring
builder.Services.AddHangfire(c => c.UseSqlServerStorage(connectionString));
builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);  // SQL persistence + auto-migration
    options.UseHangfire();                   // Hangfire job bridge
    options.AddFlow<OrderFulfillmentFlow>(); // Register your flow(s)
});

builder.Services.AddStepHandler<FetchOrdersStep>("FetchOrders");
builder.Services.AddStepHandler<SubmitToWmsStep>("SubmitToWms");

builder.Services.AddFlowDashboard(builder.Configuration); // Optional dashboard

app.UseHangfireDashboard("/hangfire");
app.MapFlowDashboard("/flows");  // Dashboard at /flows
```

```csharp
// OrderFulfillmentFlow.cs — a code-defined flow manifest
public sealed class OrderFulfillmentFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("a1b2c3d4-...");  // fixed GUID, never NewGuid()
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"]  = new TriggerMetadata { Type = TriggerType.Manual },
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?> { ["webhookSlug"] = "order-fulfillment" }
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

## 3. Core Concepts

### Flow

A **Flow** is a named, versioned workflow definition that holds:
- **Triggers** — how the flow starts: `Manual`, `Cron`, or `Webhook`
- **Steps** — an ordered DAG of work units connected via `runAfter` declarations

Flows are C# classes that implement `IFlowDefinition`. They are registered at startup via `options.AddFlow<T>()`, synced to the database (so the dashboard can discover them), and survive app restarts without creating duplicate records — as long as the `Id` GUID is fixed.

### Step

Each step has:
- `Type` — a logical string name that maps to a registered `IStepHandler`
- `RunAfter` — declares which previous steps must reach which statuses before this step executes
- `Inputs` — a dictionary of static values or [trigger expressions](#6-expression-reference)

Steps are executed by Hangfire background jobs — one Hangfire job per step. If a step fails, the run stops at that point. The dashboard **Retry** button re-enqueues the failed step without restarting the whole run.

### RunId

Each time a flow is triggered, a new `RunId` (GUID) is generated. All trigger data, step inputs, step outputs, and events are stored keyed by `RunId`. The dashboard **Runs** page lists and filters runs by status, flow, or full-text search.

### Trigger types

| Type | How to start | Required inputs |
|------|-------------|-----------------|
| `TriggerType.Manual` | Dashboard button or `POST /flows/api/flows/{id}/trigger` | None |
| `TriggerType.Cron` | Hangfire recurring job on a cron schedule | `cronExpression` (Hangfire cron syntax, e.g. `"0 2 * * *"`) |
| `TriggerType.Webhook` | External `POST /flows/api/webhook/{slug}` | `webhookSlug` (URL path segment); optional `webhookSecret` (validated via `X-Webhook-Key` header) |

Cron triggers are registered as Hangfire `RecurringJob` entries on startup. Disabling a flow in the dashboard removes its recurring jobs; re-enabling restores them.

### Step statuses

| Status | Meaning |
|--------|---------|
| `Pending` | Waiting to be enqueued (or polling — see below) |
| `Running` | Hangfire job is executing |
| `Succeeded` | Handler returned successfully |
| `Failed` | Handler threw an exception or returned a failed result |
| `Skipped` | Skipped because a prerequisite did not reach the required status |

---

## 4. Defining Flows

### 4.1 Flow definition skeleton

```csharp
public sealed class MyFlow : IFlowDefinition
{
    // ⚠ Always use a fixed GUID literal — never Guid.NewGuid().
    // Guid.NewGuid() would create a new DB row on every restart.
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000001");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection { /* ... */ },
        Steps    = new StepCollection      { /* ... */ }
    };
}
```

### 4.2 Trigger examples

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
        ["webhookSecret"] = "your-secret-key"   // client must send X-Webhook-Key: your-secret-key
    }
}
```

### 4.3 Step dependencies (`runAfter`)

`RunAfter` maps a predecessor step key to the set of statuses that allow this step to execute:

```csharp
// Run only when validate_order succeeded:
["process_payment"] = new StepMetadata
{
    Type = "ProcessPayment",
    RunAfter = new RunAfterCollection { ["validate_order"] = [StepStatus.Succeeded] }
}

// Run on either success OR failure of process_payment (e.g. a cleanup step):
["send_notification"] = new StepMetadata
{
    Type = "SendNotification",
    RunAfter = new RunAfterCollection
    {
        ["process_payment"] = [StepStatus.Succeeded, StepStatus.Failed]
    }
}
```

Steps with no `RunAfter` are treated as the flow's initial step (the first step to execute after trigger).

### 4.4 ForEach loop steps

Use `LoopStepMetadata` to iterate over a collection and run nested steps for each item:

```csharp
["process_each_order"] = new LoopStepMetadata
{
    Type = "ForEach",
    ForEach = "@triggerBody()?.orders",  // expression or array
    ConcurrencyLimit = 3,                // max parallel child executions
    Steps = new StepCollection
    {
        ["validate_item"] = new StepMetadata { Type = "ValidateOrder" }
    }
}
```

Each iteration is enqueued as a separate Hangfire job. The `Index` property is available inside the child step handler via `step.Index`.

---

## 5. Step Handlers

### 5.1 Plain handler (`IStepHandler<TInput>`)

```csharp
// Input class — fields are deserialized from the step's Inputs dictionary at runtime
public sealed class ValidateOrderInput
{
    public string OrderId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
}

// Handler class
public sealed class ValidateOrderHandler : IStepHandler<ValidateOrderInput>
{
    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance<ValidateOrderInput> step)
    {
        var orderId = step.Inputs.OrderId;  // resolved from expression or static value
        // ... validate ...
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

### 5.2 Accessing current run context from DI

Inject `IExecutionContextAccessor` to read the current `RunId` and trigger data from anywhere in the DI graph (e.g. in a repository class called by your handler):

```csharp
public sealed class MyHandler : IStepHandler<MyInput>
{
    private readonly IExecutionContextAccessor _contextAccessor;
    private readonly IOutputsRepository _outputs;

    public MyHandler(IExecutionContextAccessor contextAccessor, IOutputsRepository outputs)
    {
        _contextAccessor = contextAccessor;
        _outputs = outputs;
    }

    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext context, IFlowDefinition flow, IStepInstance<MyInput> step)
    {
        var runId = _contextAccessor.Context!.RunId;
        var previousOutput = await _outputs.GetStepOutputAsync(runId, "previous_step_key");
        // ...
    }
}
```

### 5.3 Pollable handler (`PollableStepHandler<TInput>`)

For steps that wait on an external system to complete an async operation (e.g. a batch job, a payment confirmation, a carrier status update), extend `PollableStepHandler<TInput>`:

```csharp
public sealed class CheckJobStatusInput : IPollableInput
{
    public string JobId { get; set; } = string.Empty;

    // Polling contract — configure via step Inputs in the manifest
    public bool PollEnabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 10;
    public int PollTimeoutSeconds { get; set; } = 300;
    public int PollMinAttempts { get; set; } = 1;
    public string? PollConditionPath { get; set; }
    public object? PollConditionEquals { get; set; }

    // Internal poll state — persisted between attempts (do not rename these properties)
    [JsonPropertyName("__pollStartedAtUtc")]
    public string? PollStartedAtUtc { get; set; }

    [JsonPropertyName("__pollAttempt")]
    public int? PollAttempt { get; set; }
}

public sealed class CheckJobStatusHandler : PollableStepHandler<CheckJobStatusInput>
{
    private readonly IHttpClientFactory _http;
    public CheckJobStatusHandler(IHttpClientFactory http) => _http = http;

    // Called on each poll attempt. Return the raw JSON from the external system.
    protected override async ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance<CheckJobStatusInput> step)
    {
        var client = _http.CreateClient("ExternalApi");
        var json = await client.GetStringAsync($"/jobs/{step.Inputs.JobId}");
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
        ["jobId"]                = "@triggerBody()?.jobId",
        ["pollEnabled"]          = true,
        ["pollIntervalSeconds"]  = 10,
        ["pollTimeoutSeconds"]   = 300,
        ["pollMinAttempts"]      = 1,
        ["pollConditionPath"]    = "status",      // navigate the JSON response
        ["pollConditionEquals"]  = "completed"    // succeed when status == "completed"
    }
}
```

`PollableStepHandler` manages automatically:
- Tracking `PollAttempt` and `PollStartedAtUtc` in the persisted step inputs
- Returning `StepStatus.Pending` + `DelayNextStep` when the condition is not yet met
- Returning `StepStatus.Failed` when `PollTimeoutSeconds` is exceeded
- Evaluating `PollConditionPath` (dot-notation JSON path) against the response

---

## 6. Expression Reference

Step `Inputs` values that start with `@` are evaluated at runtime against the current trigger data.

| Expression | Returns |
|---|---|
| `@triggerBody()` | Full trigger payload (`JsonElement`) |
| `@triggerBody()?.field` | A top-level field from the payload (null-safe) |
| `@triggerBody()?.nested.child` | Nested field with dot notation |
| `@triggerBody()?.items[0].name` | Array element access |
| `@triggerHeaders()` | All captured trigger headers as a dictionary |
| `@triggerHeaders()['X-Request-Id']` | A specific header value (case-insensitive) |

Expressions can appear as any string value in the `Inputs` dictionary. Non-matching strings are passed through as-is.

**Excluded headers** (never captured): `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `X-Webhook-Key`, `Connection`, `Transfer-Encoding`, `Upgrade`, `Content-Length`.

---

## 7. Running the Sample App

The sample app (`samples/FlowOrchestrator.SampleApp`) demonstrates four flows that model an e-commerce order lifecycle:

| Flow | ID | Triggers | Demonstrates |
|---|---|---|---|
| `HelloWorldFlow` | `...0001` | Manual, Cron every minute | Minimal sequential steps |
| `OrderFulfillmentFlow` | `...0002` | Manual, Webhook `/order-fulfillment` | DB query → polling API → save result |
| `ShipmentTrackingFlow` | `...0003` | Manual | Polling pattern (Pending → Pending → Succeeded) |
| `PaymentEventFlow` | `...0004` | Webhook `/payment-event` | `@triggerBody()` expression extraction |

### Option A — Via .NET Aspire (recommended, requires Docker Desktop)

```powershell
dotnet run --project .\FlowOrchestrator.AppHost\FlowOrchestrator.AppHost.csproj
```

Aspire spins up a **SQL Server 2022** container and starts the sample app with the connection string injected automatically. Open the **Aspire dashboard** at `http://localhost:18888` (no login in dev mode) to see resource health and the sample app URL.

### Option B — Local (no Docker, using LocalDB)

1. Add `appsettings.Development.json` in `samples/FlowOrchestrator.SampleApp/`:

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

2. Run:

```powershell
dotnet run --project .\samples\FlowOrchestrator.SampleApp\FlowOrchestrator.SampleApp.csproj
```

The app starts on `http://localhost:5201` by default. Open `http://localhost:5201/flows` for the dashboard.

---

## 8. Dashboard Guide

Open `http://localhost:5201/flows` (or your configured base path) to access the dashboard.

### Pages

**Overview** — Stats cards (registered flows, active runs, completed/failed today, scheduled jobs). Quick links to recent flows and runs.

**Flows** — Catalog of all registered flows. Click any flow to see:
- Manifest details (ID, name, version, enabled/disabled status)
- Steps table (key, type, inputs, runAfter dependencies)
- Triggers table with webhook URL and a **Copy** button
- **Schedule** tab: recurring cron jobs with next/last execution, inline cron expression editor, pause/resume, and trigger-now controls
- DAG graph (step dependency graph rendered as SVG)
- Raw JSON manifest viewer
- **Enable/Disable** toggle and **Trigger** button

**Runs** — Filterable run list (by flow, status, or free-text search). Search matches run fields (`id`, `flowName`, `status`, `triggerKey`) and step trace fields (`stepKey`, `errorMessage`, `outputJson`). Click a run to see the step-by-step timeline with timing, inputs, outputs, and errors. Failed steps show a **Retry** button — clicking it re-enqueues the step via Hangfire and resets the run status to **Running**.

**Scheduled** — Dedicated page for Hangfire recurring jobs: flow name, trigger key, cron expression, next/last execution, last status. Actions: trigger immediately, pause, resume, and inline cron expression editing.

### Triggering flows for testing

**From the dashboard:**
1. Go to **Flows** → click a flow card.
2. Click **Trigger** (optionally paste a JSON body).
3. Switch to **Runs** to monitor execution.

**Via the REST API:**
```http
POST http://localhost:5201/flows/api/flows/00000000-0000-0000-0000-000000000002/trigger
Content-Type: application/json

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

## 9. REST API Reference

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
| `POST` | `/flows/api/runs/{runId}/steps/{stepKey}/retry` | Retry a failed step |
| `GET` | `/flows/api/schedules` | List recurring jobs with status |
| `POST` | `/flows/api/schedules/{jobId}/trigger` | Trigger a recurring job immediately |
| `POST` | `/flows/api/schedules/{jobId}/pause` | Pause a recurring job |
| `POST` | `/flows/api/schedules/{jobId}/resume` | Resume a paused recurring job |
| `PUT` | `/flows/api/schedules/{jobId}/cron` | Update cron expression (`{ "cronExpression": "..." }`) |
| `GET` | `/hangfire` | Hangfire job queue dashboard |

---

## 10. Architecture

```
┌────────────────────────────────────────────────────────────────┐
│                       Your ASP.NET Core app                    │
│  AddFlowOrchestrator()   AddStepHandler<T>()   MapFlowDashboard│
└───────────┬────────────────────────┬──────────────────────┬────┘
            │                        │                      │
   ┌────────▼────────┐    ┌──────────▼──────────┐  ┌───────▼──────────┐
   │ FlowOrchestrator│    │  FlowOrchestrator    │  │  FlowOrchestrator│
   │    .Hangfire    │    │    .SqlServer        │  │   .Dashboard     │
   │                 │    │                      │  │                  │
   │  HangfireFlow   │    │  SqlFlowStore        │  │  REST API        │
   │  Orchestrator   │    │  SqlFlowRunStore     │  │  SPA at /flows   │
   │  DefaultStep    │    │  SqlOutputsRepo      │  │  Basic Auth      │
   │  Executor       │    │  SqlMigrator         │  │  Webhook endpt   │
   │  ForEachStep    │    └──────────┬───────────┘  └──────────────────┘
   │  Handler        │               │ Dapper / SQL Server
   │  FlowSync       │    ┌──────────▼───────────┐
   │  HostedService  │    │  FlowOrchestrator    │
   └────────┬────────┘    │      .Core           │
            │ Hangfire     │                      │
   ┌────────▼────────┐    │  IFlowDefinition     │
   │  Hangfire Jobs  │    │  IStepHandler<T>     │
   │  (SQL Server)   │    │  PollableStep        │
   └─────────────────┘    │  Handler<T>          │
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
- `IFlowStore` / `IFlowRunStore` / `IOutputsRepository` — storage abstractions (swap freely)
- In-memory implementations for testing / lightweight scenarios

**`FlowOrchestrator.Hangfire`** — Hangfire integration layer.
- `HangfireFlowOrchestrator` — `TriggerAsync` (creates run, enqueues first step), `RunStepAsync` (executes one step, enqueues next), `RetryStepAsync`
- `DefaultStepExecutor` — resolves `@triggerBody()` / `@triggerHeaders()` expressions, finds and calls the matching `IStepHandler`
- `ForEachStepHandler` — built-in handler for `LoopStepMetadata` — fans out child step jobs
- `FlowSyncHostedService` — on startup: syncs code-defined flows to `IFlowStore`, registers Hangfire recurring jobs for Cron triggers
- `RecurringTriggerSync` — keeps Hangfire recurring jobs in sync when flows are enabled/disabled at runtime

**`FlowOrchestrator.SqlServer`** — Dapper-based SQL Server persistence.
- `SqlFlowStore`, `SqlFlowRunStore`, `SqlOutputsRepository`
- `FlowOrchestratorSqlMigrator` — `IHostedService` that auto-creates tables on startup:
  `FlowDefinitions`, `FlowRuns`, `FlowSteps`, `FlowStepAttempts`, `FlowOutputs`

**`FlowOrchestrator.Dashboard`** — Built-in monitoring UI and REST API.
- Minimal API endpoints for flow catalog, triggering, run history, retry, schedules
- Embedded single-page application (HTML/JS/CSS) served at the configured base path
- Optional Basic Auth middleware; webhook endpoint uses a separate `webhookSecret`

### Execution flow (step-by-step)

1. A trigger fires (dashboard button, cron schedule, or webhook POST).
2. `HangfireFlowOrchestrator.TriggerAsync` generates a `RunId`, saves trigger data and headers, calls `IFlowExecutor.TriggerFlow()` to get the first step, and enqueues it as a Hangfire job.
3. Hangfire fires `RunStepAsync` → `DefaultStepExecutor` resolves input expressions, dispatches to the matching `IStepHandler`, and persists the output.
4. `IFlowExecutor.GetNextStep()` evaluates `runAfter` conditions. If satisfied → enqueues the next step. If all steps are complete → marks the run as Succeeded/Failed.
5. On failure → the run stops. The dashboard **Retry** button calls `RetryStepAsync`, which resets the step state and re-enqueues from that point.
6. For polling steps → if `FetchAsync` returns a result where the condition is not yet met, `PollableStepHandler` returns `Pending` + `DelayNextStep`. Hangfire schedules the re-run after the delay.

### Swapping storage

Register your own implementations of `IFlowStore`, `IFlowRunStore`, and `IOutputsRepository` directly on `options.Services` (skip `UseSqlServer()`):

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseHangfire();
    options.AddFlow<MyFlow>();
    // Custom storage:
    options.Services.AddSingleton<IFlowStore, MyRedisFlowStore>();
    options.Services.AddSingleton<IFlowRunStore, MyRedisFlowRunStore>();
    options.Services.AddSingleton<IOutputsRepository, MyRedisOutputsRepository>();
});
```

---

## 11. Solution Layout

```
src/
  FlowOrchestrator.Core          Core abstractions, execution engine, in-memory stores
  FlowOrchestrator.Hangfire      Hangfire bridge, DI extensions, built-in step handlers
  FlowOrchestrator.SqlServer     Dapper/SQL Server persistence + auto-migrator
  FlowOrchestrator.Dashboard     REST API + embedded HTML/JS dashboard SPA

samples/
  FlowOrchestrator.SampleApp     Runnable ASP.NET Core demo (OrderHub scenario)

FlowOrchestrator.AppHost/        .NET Aspire host (SQL Server container + SampleApp)

tests/
  FlowOrchestrator.Core.Tests    Unit tests (xUnit + FluentAssertions + NSubstitute)
  FlowOrchestrator.Hangfire.Tests
```

---

## 12. License

MIT — see the `LICENSE` file.
