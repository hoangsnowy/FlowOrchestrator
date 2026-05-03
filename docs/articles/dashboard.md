# Dashboard

The FlowOrchestrator dashboard is an embedded REST API + single-page application served directly from your ASP.NET Core app. No separate server or deployment is required.

## Setup

```bash
dotnet add package FlowOrchestrator.Dashboard
```

```csharp
// In Program.cs
builder.Services.AddFlowDashboard(builder.Configuration);

var app = builder.Build();
app.MapFlowDashboard("/flows");  // SPA at /flows, API at /flows/api/**
```

`AddFlowDashboard` has three overloads:

```csharp
// From IConfiguration — reads FlowDashboard section from appsettings.json
builder.Services.AddFlowDashboard(builder.Configuration);

// Inline configuration via delegate
builder.Services.AddFlowDashboard(options =>
{
    options.Title = "OrderHub";
    options.BasicAuth.Username = "admin";
    options.BasicAuth.Password = "secret";
});

// No arguments — defaults only (no auth, default title)
builder.Services.AddFlowDashboard();
```

---

## Configuration

```json
{
  "FlowDashboard": {
    "Title": "OrderHub Workflows",
    "Subtitle": "Production",
    "LogoUrl": "/images/logo.png",
    "BasicAuth": {
      "Enabled": true,
      "Username": "admin",
      "Password": "changeme"
    }
  }
}
```

| Option | Default | Description |
|---|---|---|
| `Title` | `"FlowOrchestrator"` | Browser tab title and navbar heading |
| `Subtitle` | — | Small label next to the title (e.g. environment name) |
| `LogoUrl` | — | URL of a custom logo image |
| `BasicAuth.Enabled` | `false` | Enable HTTP Basic Auth on all dashboard routes |
| `BasicAuth.Username` | — | Required when `BasicAuth.Enabled = true` |
| `BasicAuth.Password` | — | Required when `BasicAuth.Enabled = true` |

---

## Dashboard Pages

### Overview

Landing page showing run statistics: total runs, active runs, succeeded today, failed today.

### Flows

Lists all registered flows. Each row shows flow name, version, last run status, trigger types, and enabled/disabled state.

Click a flow to open the **detail view**:
- Manifest details: triggers, step list, DAG graph visualization
- Enable/Disable toggle
- Trigger button (manual trigger)
- Recent runs table

### Runs

Filterable list of all flow runs with columns for flow name, status, trigger, start/end times.

Filter parameters:
- Flow (dropdown)
- Status (Pending / Running / Succeeded / Failed / Cancelled / TimedOut)
- Search (free text)

Click a run to open the **run timeline**:
- Step-by-step execution timeline with status badges (`Succeeded`, `Failed`, `Running`, `Pending`, **`Blocked`** — step could not run because a dependency did not meet its `runAfter` condition)
- Input/output JSON for each step
- Retry button on failed steps
- Cancel button for running steps

### Scheduled

Lists all Hangfire recurring jobs registered by cron triggers.

Actions per job:
- **Trigger Now** — fires the job immediately outside of the schedule
- **Pause / Resume** — suspends or resumes the recurring schedule
- **Edit Cron** — inline cron expression editor (persisted when `Scheduler.PersistOverrides = true`)

---

## REST API Reference

All endpoints are under the base path configured in `MapFlowDashboard`. Examples below use `/flows` as the base.

### Flow Catalog

| Method | Path | Description |
|---|---|---|
| `GET` | `/flows/api/flows` | List all registered flows |
| `GET` | `/flows/api/flows/{id}` | Get flow definition and manifest |
| `POST` | `/flows/api/flows/{id}/trigger` | Manually trigger a flow |
| `POST` | `/flows/api/flows/{id}/enable` | Enable the flow and restore cron jobs |
| `POST` | `/flows/api/flows/{id}/disable` | Disable the flow and remove cron jobs |
| `GET` | `/flows/api/handlers` | List registered step handler type names |

### Webhook Endpoint

```http
POST /flows/api/webhook/{webhookSlug}
Content-Type: application/json
X-Webhook-Key: {secret}          (required if webhookSecret was configured)
Idempotency-Key: {unique-key}    (optional — prevents duplicate runs)

{ ...trigger payload... }
```

### Run Monitoring

| Method | Path | Description |
|---|---|---|
| `GET` | `/flows/api/runs` | List runs — queryable: `?flowId=`, `?status=`, `?search=`, `?skip=`, `?take=` |
| `GET` | `/flows/api/runs/active` | List currently-running runs |
| `GET` | `/flows/api/runs/stats` | Aggregate statistics for the dashboard overview |
| `GET` | `/flows/api/runs/{id}` | Run detail with trigger headers/body |
| `GET` | `/flows/api/runs/{id}/steps` | All step details for a run |
| `GET` | `/flows/api/runs/{runId}/events` | Event stream for the run (requires `EnableEventPersistence = true`) |
| `GET` | `/flows/api/runs/{runId}/control` | Timeout, cancellation, idempotency state |
| `POST` | `/flows/api/runs/{runId}/cancel` | Request cooperative cancellation |
| `POST` | `/flows/api/runs/{runId}/steps/{stepKey}/retry` | Retry a failed step |

### Realtime Event Stream (SSE)

| Method | Path | Description |
|---|---|---|
| `GET` | `/flows/api/events/stream` | Server-Sent Events stream of run / step lifecycle events |

The dashboard SPA subscribes to this endpoint via `EventSource`; state
changes (`run.started`, `step.completed`, `step.retried`, `run.completed`)
are pushed within milliseconds of the engine recording them. Polling
runs only as a fallback when the stream stalls (>20 s without events,
or 3+ failed reconnects) and stops on the next live event.

- **Content-Type:** `text/event-stream; charset=utf-8`
- **No compression:** Brotli/Gzip would buffer chunks; the endpoint
  always writes uncompressed.
- **Heartbeat:** every 15 s as a comment line — beats nginx's 60 s and
  Azure Front Door's 240 s default proxy idle timeouts.
- **Filter:** `?runId={guid}` scopes the stream to a single run for the
  detail page.

Custom realtime consumers (log sinks, custom UIs, Slack notifiers) can
implement `IFlowEventNotifier` from `FlowOrchestrator.Core.Notifications`
and replace the dashboard's broadcaster registration. The default
`NoopFlowEventNotifier` makes apps without the dashboard pay zero overhead.

### Schedule Management

| Method | Path | Description |
|---|---|---|
| `GET` | `/flows/api/schedules` | List Hangfire recurring jobs |
| `POST` | `/flows/api/schedules/{jobId}/trigger` | Trigger a recurring job immediately |
| `POST` | `/flows/api/schedules/{jobId}/pause` | Pause a recurring job |
| `POST` | `/flows/api/schedules/{jobId}/resume` | Resume a paused recurring job |
| `PUT` | `/flows/api/schedules/{jobId}/cron` | Update the cron expression |

---

## Retry a Failed Step

From the dashboard, open a failed run and click **Retry** on the failed step. This calls:

```http
POST /flows/api/runs/{runId}/steps/{stepKey}/retry
```

FlowOrchestrator resets the step to `Pending`, preserves all prior step outputs, and re-dispatches the step via the runtime adapter from the failure point. Steps that already succeeded are not re-executed.

## Cancel a Running Flow

```http
POST /flows/api/runs/{runId}/cancel
```

Sets a cancellation flag. The next execution of this run by the runtime adapter picks up the flag and the `CancellationToken` in `IExecutionContext` is cancelled. Step handlers that check `ctx.CancellationToken` will stop gracefully. The run is marked `Cancelled` after the in-flight step completes.
