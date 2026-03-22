## FlowOrchestrator

FlowOrchestrator is an open-source .NET library that lets you **orchestrate workflows / background jobs from a declarative JSON manifest**, executed on **Hangfire**, with **SQL persistence** and a **built-in dashboard** to configure and monitor flows.

---

## Compatibility

- NuGet packages (`FlowOrchestrator.Core`, `FlowOrchestrator.Hangfire`, `FlowOrchestrator.SqlServer`, `FlowOrchestrator.Dashboard`) target: `net8.0`, `net9.0`, `net10.0`.
- Demo projects (`FlowOrchestrator.SampleApp`, `FlowOrchestrator.AppHost`) currently run on `net8.0`.

---

## 1. High-level design

- **Flow**
  A flow is a workflow definition, described by a **JSON manifest**:
  - Triggers: how a flow starts (manual, cron schedule, etc.).
  - Steps: ordered actions (validate order, process payment, send email, etc.).
  - Dependencies: `runAfter` decides which step runs after which and for which statuses.

- **Step**
  Each step has:
  - `type`: logical name (e.g. `ValidateOrder`, `ProcessPayment`).
  - `runAfter`: declarative dependencies on previous steps and their statuses.
  - `inputs`: expressions referencing trigger body/headers or previous step outputs.

- **Execution engine (`FlowOrchestrator.Core`)**
  - `IFlowExecutor`: decides which step to run next.
  - `IStepExecutor`: resolves inputs, invokes the right handler, stores outputs.
  - `IOutputsRepository`: stores trigger data/headers, step inputs/outputs, events.
  - `IFlowStore` / `IFlowRunStore`: abstractions for persistence of flows and run history.
  - `IFlowRepository`: in-memory registry of code-defined `IFlowDefinition` instances.

- **SQL persistence (`FlowOrchestrator.SqlServer`)**
  - `SqlFlowStore` / `SqlFlowRunStore`: Dapper-based implementations against SQL Server.
  - `FlowOrchestratorSqlMigrator`: auto-creates `FlowDefinitions`, `FlowRuns`, `FlowSteps` tables on startup.

- **Background processing (`FlowOrchestrator.Hangfire`)**
  - `IHangfireFlowTrigger`: entry point when a trigger fires; enqueues the first step.
  - `IHangfireStepRunner`: Hangfire job that executes a single step and enqueues the next.
  - `RetryStepAsync`: re-enqueues a failed step, resetting its state and resuming the flow from that point.
  - `TriggerByScheduleAsync`: used by Hangfire recurring jobs to fire Cron triggers on schedule.
  - `FlowSyncHostedService`: on startup, syncs code-defined flows into `IFlowStore` and registers Hangfire `RecurringJob` entries for any `Cron` triggers.
  - `IRecurringTriggerSync`: syncs recurring jobs when a flow is enabled/disabled at runtime.
  - `AddFlow<T>()`: register code-defined flows into the DI container and flow catalog.

- **Dashboard (`FlowOrchestrator.Dashboard`)**
  - Built-in HTML/JS SPA served at `/flows` with Hangfire-style light UI and sidebar navigation.
  - **Overview** page: stats cards (registered flows, active runs, completed/failed today).
  - **Flows** page: catalog of all registered flows with manifest viewer, triggers, steps, DAG graph, enable/disable, trigger button.
  - **Runs** page: filterable run list with step timeline detail view. Failed steps show a **Retry** button to re-run from that step.
  - REST API endpoints for flow CRUD, triggering, run monitoring, and step retry.

---

## 2. Solution layout

```
src/
  FlowOrchestrator.Core        – abstractions, execution engine, storage interfaces
  FlowOrchestrator.Hangfire    – Hangfire bridge + DI extensions + flow registration
  FlowOrchestrator.SqlServer   – Dapper-based SQL Server persistence (FlowStore, FlowRunStore)
  FlowOrchestrator.Dashboard   – REST API + built-in HTML/JS dashboard SPA

samples/
  FlowOrchestrator.SampleApp   – runnable ASP.NET Core demo app

FlowOrchestrator.AppHost/      – .NET Aspire host (SQL container + SampleApp)
```

---

## 3. Running the solution

### Option A – Via .NET Aspire (recommended, requires Docker)

```powershell
dotnet run --project .\FlowOrchestrator.AppHost\FlowOrchestrator.AppHost.csproj
```

Aspire will:
- Spin up a **SQL Server 2022** container automatically.
- Start **FlowOrchestrator.SampleApp** and inject the connection string.
- Open the **Aspire dashboard** at `http://localhost:18888` (no login required in dev mode).

From the Aspire dashboard you can:
- See resource health (SQL container, web app).
- Click the URL of `flow-orchestrator-web` to open the app.

> **Prerequisites:** Docker Desktop must be running before launching the Aspire host.

---

### Option B – Local (no Docker, using LocalDB)

1. Add the connection string to `samples/FlowOrchestrator.SampleApp/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "FlowOrchestrator": "Server=(localdb)\\mssqllocaldb;Database=FlowOrchestrator;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "FlowDashboard": {
    "Branding": {
      "Title": "FlowOrchestrator",
      "Subtitle": "FlowRUN",
      "LogoUrl": "/icon.png"
    },
    "BasicAuth": {
      "Username": "admin",
      "Password": "change-me",
      "Realm": "FlowOrchestrator Dashboard"
    }
  }
}
```

`FlowDashboard:Branding` is optional. If `LogoUrl` is empty or invalid, dashboard falls back to the default logo icon.
`FlowDashboard:BasicAuth` is optional. Leave `Username`/`Password` empty to disable dashboard auth.

2. Run the app:

```powershell
dotnet run --project .\samples\FlowOrchestrator.SampleApp\FlowOrchestrator.SampleApp.csproj
```

The app starts on `http://localhost:5201` by default.

---

## 4. Using the dashboard

Open `http://localhost:5201/flows` to access the built-in dashboard.
If `FlowDashboard:BasicAuth` is configured, the browser will prompt for HTTP Basic credentials.
Webhook endpoint (`/flows/api/webhook/{idOrSlug}`) is intentionally not protected by dashboard basic auth; use `webhookSecret` for webhook security.
Manual trigger and webhook endpoints capture request body plus non-sensitive headers for input expressions (see section 5.5).

### Pages

- **Overview**: Summary stats — registered flows, active runs, completed/failed today, scheduled jobs count. Quick links to recent flows and runs.
- **Flows**: Catalog of all registered flows. Click any flow to see:
  - Manifest details (ID, name, version, status)
  - Steps table (key, type, inputs, runAfter dependencies)
  - Triggers table
  - **Schedule** tab showing recurring jobs for the flow with next/last execution, cron expression editing, trigger now, and pause controls
  - DAG visualization (step dependency graph rendered as SVG)
  - Raw JSON manifest viewer
  - Enable/disable toggle and trigger button
- **Runs**: Filterable run list (by flow, by status, by search text). Search supports run fields (`id`, `flowName`, `triggerKey`, `status`, `backgroundJobId`) and step trace fields (`stepKey`, `errorMessage`, `outputJson`). Click a run to see the step-by-step timeline with timing, outputs, and errors. Failed steps show a **Retry** button inline.
- **Scheduled**: Dedicated page listing all Hangfire recurring jobs with flow name, trigger key, cron expression, next/last execution, and last status. Actions: trigger immediately, pause, and inline cron expression editing.

### Triggering a flow from the dashboard

1. Go to **Flows** → click a flow card.
2. Click the **Trigger** button.
3. Switch to the **Runs** page to monitor execution.

### Reusable polling step handler

`FlowOrchestrator.Core` provides a reusable polling abstraction for step handlers:
- `PollableStepHandler<TInput>` handles retry scheduling (`Pending` + `DelayNextStep`), timeout, min-attempt checks, and poll-state reset.
- `IPollableInput` defines polling contract fields used by the base handler.

```csharp
public sealed class CheckJobStatusStep : PollableStepHandler<CheckJobStatusInput>
{
    protected override async ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext ctx, IFlowDefinition flow, IStepInstance<CheckJobStatusInput> step)
    {
        var payload = await CallExternalServiceAsync(step.Inputs.JobId);
        return (payload, true);
    }
}

public sealed class CheckJobStatusInput : IPollableInput
{
    public string JobId { get; set; } = string.Empty;
    public bool PollEnabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 10;
    public int PollTimeoutSeconds { get; set; } = 300;
    public int PollMinAttempts { get; set; } = 1;
    public string? PollConditionPath { get; set; }
    public object? PollConditionEquals { get; set; }

    [JsonPropertyName("__pollStartedAtUtc")]
    public string? PollStartedAtUtc { get; set; }

    [JsonPropertyName("__pollAttempt")]
    public int? PollAttempt { get; set; }
}
```

### Polling demo flow

`PollingDemoFlow` (`00000000-0000-0000-0000-000000000003`) demonstrates this abstraction through `CallExternalApiStep : PollableStepHandler<CallExternalApiStepInput>`:
- `pollEnabled = true`
- `pollMinAttempts = 3` (forces at least 3 poll attempts before success)
- `pollIntervalSeconds = 5`

This lets you observe **Pending -> Pending -> Succeeded** progression in the Runs timeline.
Run detail also shows an attempt badge (for retries/polls) and a `Step Attempts` panel with per-attempt input/output/status history.

### Retrying a failed step

1. Go to **Runs** → click the run that contains the failed step.
2. In the step timeline, click the **Retry** button next to the failed step.
3. Confirm the dialog — the step is re-enqueued via Hangfire and the run status resets to **Running**.
4. Downstream steps continue normally if the retried step succeeds.

---

## 5. Testing a workflow via API

### 5.1 Trigger a flow by ID

```http
POST http://localhost:5201/flows/api/flows/{flowId}/trigger
Content-Type: application/json

{}
```

PowerShell:
```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5201/flows/api/flows/00000000-0000-0000-0000-000000000001/trigger" `
  -ContentType "application/json" -Body '{}'
```

### 5.2 Inspect the run

```http
GET http://localhost:5201/flows/api/runs/{runId}
```

### 5.3 List and filter runs

```http
GET http://localhost:5201/flows/api/runs
GET http://localhost:5201/flows/api/runs?flowId={flowId}
GET http://localhost:5201/flows/api/runs?status=Failed&search=timeout&includeTotal=true&skip=0&take=20
GET http://localhost:5201/flows/api/runs/active
GET http://localhost:5201/flows/api/runs/stats
```

### 5.4 Trigger via webhook (for external clients)

```http
POST http://localhost:5201/flows/api/webhook/{flowId}
Content-Type: application/json
X-Webhook-Key: your-secret-key   # Required only if webhookSecret is configured

{ "orderId": "123", "customerId": "abc" }
```

Or by slug (when `webhookSlug` is set in the trigger inputs):

```http
POST http://localhost:5201/flows/api/webhook/order-webhook
Content-Type: application/json

{}
```

### 5.5 Trigger expression reference (`inputs`)

`FlowOrchestrator.Hangfire` supports trigger expressions in step inputs:

```json
{
  "orderId": "@triggerBody()?.orderId",
  "requestId": "@triggerHeaders()['X-Request-Id']",
  "allHeaders": "@triggerHeaders()"
}
```

- `@triggerBody()` returns the full trigger payload as JSON.
- `@triggerBody()?.path.to.value` returns a specific value from trigger payload.
- `@triggerHeaders()` returns all captured trigger headers as JSON object.
- `@triggerHeaders()['Header-Name']` (or `["Header-Name"]`) returns a specific header value.

Captured headers are case-insensitive and exclude sensitive/transport headers:
`Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `X-Webhook-Key`, `Connection`, `Transfer-Encoding`, `Upgrade`, `Content-Length`.

---

## 6. Writing your own flow

### 6.1 Define a flow

```csharp
public sealed class MyFlow : IFlowDefinition
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = "Manual" },
            ["nightly"] = new TriggerMetadata
            {
                Type = "Cron",
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 2 * * *" }
            }
        },
        Steps = new StepCollection
        {
            ["validateOrder"] = new StepMetadata
            {
                Type = "ValidateOrder",
                Inputs = new Dictionary<string, object?>
                {
                    ["orderId"] = "@triggerBody()?.orderId",
                    ["requestId"] = "@triggerHeaders()['X-Request-Id']"
                }
            },
            ["processPayment"] = new StepMetadata
            {
                Type = "ProcessPayment",
                RunAfter = new RunAfterCollection { ["validateOrder"] = ["Succeeded"] }
            }
        }
    };
}
```

**Trigger types:**

| Type | Description | Required inputs |
|------|-------------|-----------------|
| `Manual` | Triggered by dashboard button or API call | None |
| `Cron` | Runs on a recurring schedule via Hangfire `RecurringJob` | `cronExpression` (Hangfire cron syntax, e.g. `*/5 * * * *`) |
| `Webhook` | Triggered by external HTTP POST (e.g. customer webhook) | Optional: `webhookSlug` (URL path), `webhookSecret` (for `X-Webhook-Key` validation). Trigger body and non-sensitive headers are available via expressions |

Cron triggers are automatically registered as Hangfire recurring jobs on startup. Disabling a flow via the dashboard removes its recurring jobs; re-enabling restores them.

**Webhook trigger:** Use `POST /flows/api/webhook/{flowId}` or `POST /flows/api/webhook/{slug}` to trigger from external systems. If `webhookSecret` is set in the trigger inputs, clients must send `X-Webhook-Key: <secret>` or `Authorization: Bearer <secret>`. The dashboard shows the webhook URL and a Copy button in the Triggers tab. `X-Webhook-Key` and other sensitive headers are intentionally excluded from captured trigger headers.

### 6.2 Implement step handlers

```csharp
public sealed class ValidateOrderHandler : IStepHandler
{
    public ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        return ValueTask.FromResult<object?>(new { valid = true });
    }
}
```

### 6.3 Register in DI

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();
    options.AddFlow<MyFlow>();
});

builder.Services.AddStepHandler<ValidateOrderHandler>("ValidateOrder");
builder.Services.AddStepHandler<ProcessPaymentHandler>("ProcessPayment");
```

Flows registered via `AddFlow<T>()` are automatically synced to `IFlowStore` (SQL) on startup and appear in the dashboard's Flows catalog.

---

## 7. Dashboard API reference

All endpoints are served under the base path configured in `MapFlowDashboard(basePath)` (default: `/flows`).

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/flows/api/flows` | List all registered flow definitions |
| `GET` | `/flows/api/flows/{id}` | Get a single flow definition (with manifest) |
| `POST` | `/flows/api/flows/{id}/enable` | Enable a flow |
| `POST` | `/flows/api/flows/{id}/disable` | Disable a flow |
| `POST` | `/flows/api/flows/{id}/trigger` | Trigger a flow (creates a new run via Hangfire) |
| `POST` | `/flows/api/webhook/{idOrSlug}` | Webhook endpoint: trigger by flow ID or slug. Optional `X-Webhook-Key` header when `webhookSecret` is configured |
| `GET` | `/flows/api/handlers` | List registered step handler types |
| `GET` | `/flows/api/runs` | List runs (`?flowId=`, `?status=`, `?search=`, `?includeTotal=`, `?skip=`, `?take=`) |
| `GET` | `/flows/api/runs/active` | List currently running flows |
| `GET` | `/flows/api/runs/stats` | Dashboard statistics |
| `GET` | `/flows/api/runs/{id}` | Run detail with steps |
| `GET` | `/flows/api/runs/{id}/steps` | Steps for a specific run |
| `POST` | `/flows/api/runs/{id}/steps/{stepKey}/retry` | Retry a failed step (re-enqueues via Hangfire) |
| `GET` | `/flows/api/schedules` | List all recurring jobs with status |
| `POST` | `/flows/api/schedules/{jobId}/trigger` | Trigger a recurring job immediately |
| `POST` | `/flows/api/schedules/{jobId}/pause` | Pause (remove) a recurring job |
| `POST` | `/flows/api/schedules/{jobId}/resume` | Resume a paused recurring job |
| `PUT` | `/flows/api/schedules/{jobId}/cron` | Update cron expression (`{ "cronExpression": "..." }`) |
| `GET` | `/hangfire` | Hangfire job dashboard |

---

## 8. License

The project is licensed under the **MIT License** (see the `LICENSE` file in the repo).
