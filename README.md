## FlowOrchestrator

FlowOrchestrator is an open-source .NET library that lets you **orchestrate workflows / background jobs from a declarative JSON manifest**, executed on **Hangfire**, with **SQL persistence** and a **built-in dashboard** to configure and monitor flows.

---

## 1. High-level design

- **Flow**
  A flow is a workflow definition, described by a **JSON manifest**:
  - Triggers: how a flow starts (e.g. manual, timer, HTTP, message).
  - Steps: ordered actions (validate order, process payment, send email, etc.).
  - Dependencies: `runAfter` decides which step runs after which and for which statuses.

- **Step**
  Each step has:
  - `type`: logical name (e.g. `ValidateOrder`, `ProcessPayment`).
  - `runAfter`: declarative dependencies on previous steps and their statuses.
  - `inputs`: expressions referencing trigger/body or previous step outputs.

- **Execution engine (`FlowOrchestrator.Core`)**
  - `IFlowExecutor`: decides which step to run next.
  - `IStepExecutor`: resolves inputs, invokes the right handler, stores outputs.
  - `IOutputsRepository`: stores trigger data, step inputs/outputs, events.
  - `IFlowStore` / `IFlowRunStore`: abstractions for persistence of flows and run history.
  - `IFlowRepository`: in-memory registry of code-defined `IFlowDefinition` instances.

- **SQL persistence (`FlowOrchestrator.SqlServer`)**
  - `SqlFlowStore` / `SqlFlowRunStore`: Dapper-based implementations against SQL Server.
  - `FlowOrchestratorSqlMigrator`: auto-creates `FlowDefinitions`, `FlowRuns`, `FlowSteps` tables on startup.

- **Background processing (`FlowOrchestrator.Hangfire`)**
  - `IHangfireFlowTrigger`: entry point when a trigger fires; enqueues the first step.
  - `IHangfireStepRunner`: Hangfire job that executes a single step and enqueues the next.
  - `FlowSyncHostedService`: on startup, syncs code-defined flows into `IFlowStore`.
  - `AddFlow<T>()`: register code-defined flows into the DI container and flow catalog.

- **Dashboard (`FlowOrchestrator.Dashboard`)**
  - Built-in HTML/JS SPA served at `/flows` with sidebar navigation.
  - **Overview** page: stats cards (registered flows, active runs, completed/failed today).
  - **Flows** page: catalog of all registered flows with manifest viewer, triggers, steps, DAG graph, enable/disable, trigger button.
  - **Runs** page: filterable run list with step timeline detail view.
  - REST API endpoints for flow CRUD, triggering, and run monitoring.

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
  }
}
```

2. Run the app:

```powershell
dotnet run --project .\samples\FlowOrchestrator.SampleApp\FlowOrchestrator.SampleApp.csproj
```

The app starts on `http://localhost:5201` by default.

---

## 4. Using the dashboard

Open `http://localhost:5201/flows` to access the built-in dashboard.

### Pages

- **Overview**: Summary stats — registered flows, active runs, completed/failed today. Quick links to recent flows and runs.
- **Flows**: Catalog of all registered flows. Click any flow to see:
  - Manifest details (ID, name, version, status)
  - Steps table (key, type, inputs, runAfter dependencies)
  - Triggers table
  - DAG visualization (step dependency graph rendered as SVG)
  - Raw JSON manifest viewer
  - Enable/disable toggle and trigger button
- **Runs**: Filterable run list (by flow, by status). Click a run to see the step-by-step timeline with timing, outputs, and errors.

### Triggering a flow from the dashboard

1. Go to **Flows** → click a flow card.
2. Click the **Trigger** button.
3. Switch to the **Runs** page to monitor execution.

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
GET http://localhost:5201/flows/api/runs/active
GET http://localhost:5201/flows/api/runs/stats
```

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
            ["manual"] = new TriggerMetadata { Type = "Manual" }
        },
        Steps = new StepCollection
        {
            ["validateOrder"] = new StepMetadata
            {
                Type = "ValidateOrder",
                Inputs = new Dictionary<string, object?> { ["orderId"] = "@triggerBody()?.orderId" }
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
| `GET` | `/flows/api/handlers` | List registered step handler types |
| `GET` | `/flows/api/runs` | List runs (`?flowId=`, `?skip=`, `?take=`) |
| `GET` | `/flows/api/runs/active` | List currently running flows |
| `GET` | `/flows/api/runs/stats` | Dashboard statistics |
| `GET` | `/flows/api/runs/{id}` | Run detail with steps |
| `GET` | `/flows/api/runs/{id}/steps` | Steps for a specific run |
| `GET` | `/hangfire` | Hangfire job dashboard |

---

## 8. License

The project is licensed under the **MIT License** (see the `LICENSE` file in the repo).
