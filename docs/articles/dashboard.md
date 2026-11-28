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

`AddFlowDashboard` has four overloads:

```csharp
// From IConfiguration — reads FlowDashboard section from appsettings.json
builder.Services.AddFlowDashboard(builder.Configuration);

// Inline configuration via delegate
builder.Services.AddFlowDashboard(options =>
{
    options.Branding.Title = "OrderHub";
    options.BasicAuth.Username = "admin";
    options.BasicAuth.Password = "secret";
});

// No arguments — defaults only (no auth, default title). Without auth the dashboard is
// read-only outside the Development environment; see "Securing the dashboard".
builder.Services.AddFlowDashboard();

// Configuration + code: binds appsettings (e.g. Basic Auth) first, then applies the delegate.
// Use this when you need config-driven Basic Auth AND code-only settings such as webhook security —
// the delegate-only overload above does not read configuration.
builder.Services.AddFlowDashboard(builder.Configuration, options =>
{
    options.UseWebhookSecurity(sec => sec.UseEnforcementMode(WebhookEnforcementMode.Enforce));
});
```

---

## Configuration

```json
{
  "FlowDashboard": {
    "Branding": {
      "Title": "OrderHub Workflows",
      "Subtitle": "Production",
      "LogoUrl": "/images/logo.png"
    },
    "BasicAuth": {
      "Username": "admin",
      "Password": "changeme"
    }
  }
}
```

| Option | Default | Description |
|---|---|---|
| `Branding.Title` | `"FlowOrchestrator Dashboard"` | Browser tab title and navbar heading |
| `Branding.Subtitle` | `"Dashboard"` | Small label next to the title (e.g. environment name) |
| `Branding.LogoUrl` | — | URL of a custom logo image |
| `BasicAuth.Username` | — | Basic Auth username |
| `BasicAuth.Password` | — | Basic Auth password |
| `BasicAuth.Realm` | `"FlowOrchestrator Dashboard"` | `WWW-Authenticate` realm returned in 401 responses |
| `AllowAnonymousOperators` | `false` | Give anonymous callers operator access on a dashboard with no authentication outside `Development`. See [Securing the dashboard](dashboard-security.md#no-authentication-configured) for the risk. |

> [!NOTE]
> Basic Auth is enforced when **both** `BasicAuth.Username` and `BasicAuth.Password` are set. There is no separate `Enabled` flag — leaving either blank disables authentication.

> [!IMPORTANT]
> A dashboard with neither Basic Auth nor `Authorization` configured is **read-only outside the
> `Development` environment**: state-changing calls answer `403` and secrets in manifests and payloads are
> masked. In `Development` anonymous callers keep full access. See
> [Securing the dashboard](dashboard-security.md#no-authentication-configured).

---

## Dashboard Pages

### Overview

Landing page showing five stat cards: registered flows, active runs, completed today, failed today, and scheduled jobs.

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
- Status (Running / Succeeded / Blocked / Failed / Cancelled / Timed Out — the `Blocked` label maps to the canonical `Skipped` status)
- Search (free text)

Click a run to open the **run timeline**:
- Step-by-step execution timeline with status badges (`Succeeded`, `Failed`, `Running`, `Pending`, **`Blocked`** — step could not run because a dependency did not meet its `runAfter` condition)
- Input/output JSON for each step
- Retry button on failed steps
- Cancel button for running steps

### Scheduled

Lists all recurring cron jobs registered by cron triggers. The data comes from `IRecurringTriggerInspector`, so it is runtime-agnostic — the same tab works under the Hangfire, in-memory, and Service Bus runtimes.

Actions per job:
- **Trigger Now** — fires the job immediately outside of the schedule
- **Pause / Resume** — suspends or resumes the recurring schedule
- **Edit Cron** — inline cron expression editor (persisted when `Scheduler.PersistOverrides = true`)

### Servers

Lists every FlowOrchestrator process (replica) sharing the storage, whatever the runtime — Hangfire, in-memory
or Service Bus. Each process writes a heartbeat row to `IFlowServerStore` on start and then every
`HeartbeatInterval`; the tab shows, per server: a short server id (hover for the full one), machine, process id,
runtime, FlowOrchestrator version, when it started, how long ago its last heartbeat arrived, the number of steps
executing on it, and a status chip.

- **Active** — the latest heartbeat is at most `StaleAfter` old.
- **Stale** — the server missed `MissedHeartbeatsBeforeStale` heartbeats in a row: it crashed, hangs, or cannot
  reach the store. A process that stops gracefully removes its own row instead; a stale row is deleted by any
  replica once it has been silent for `PurgeAfter`.

The server id is the step-lease owner id (`FlowStepLeases.Owner`), and every step attempt records it, so the
step detail of a run shows which server ran each attempt (`FlowStepAttemptRecord.ServerId`; empty for attempts
written before 2.0).

SQL Server, PostgreSQL and in-memory storage track servers (`FlowServers` / `flow_servers`, created by the
migrators). A custom storage backend that does not register an `IFlowServerStore` keeps working: no heartbeat
is written and the tab says the storage does not track servers. The in-memory store is process-local, so it only
ever lists the process it lives in.

```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();
    options.Servers.HeartbeatInterval = TimeSpan.FromSeconds(15);   // default 15 s, 1 s .. 10 min
    options.Servers.MissedHeartbeatsBeforeStale = 3;                 // default 3 (2 .. 100): stale after 45 s
    options.Servers.PurgeAfter = TimeSpan.FromHours(1);              // default 1 h, must exceed StaleAfter
    // options.Servers.Enabled = false;                              // opt this process out of the registry
});
```

Timestamps come from the database clock, so replicas with skewed clocks are aged against one clock; the
dashboard computes the status against the host's `TimeProvider`. The tab polls every 5 s while it is open.

---

## REST API Reference

All endpoints are under the base path configured in `MapFlowDashboard`. Examples below use `/flows` as the base.

### Flow Catalog

| Method | Path | Description |
|---|---|---|
| `GET` | `/flows/api/flows` | List all registered flows |
| `GET` | `/flows/api/flows/{id}` | Get flow definition and manifest |
| `POST` | `/flows/api/flows/{id}/trigger` | Manually trigger a flow. `200` with `runId` + `duplicate`; **`409`** with `runId: null, disabled: true` when the flow is disabled |
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
| `GET` | `/flows/api/runs` | List runs — queryable: `?flowId=`, `?status=`, `?search=`, `?from=`, `?to=`, `?deep=`, `?skip=`, `?take=`, `?includeTotal=`. With `includeTotal=true` the response is `{ items, total, hasMore, skip, take, search }` instead of a bare array — see *Run search* below. |
| `GET` | `/flows/api/runs/active` | List currently-running runs |
| `GET` | `/flows/api/runs/stats` | Aggregate statistics for the dashboard overview |
| `GET` | `/flows/api/runs/timeseries` | Bucketed run counts for the overview charts — `?bucket=hour\|day`, `?hours=` (default 24, max 720), `?days=` (default 30, max 365), `?since=`/`?until=`, `?flowId=` |
| `GET` | `/flows/api/runs/{id}` | Run detail with trigger headers/body |
| `GET` | `/flows/api/runs/{id}/steps` | All step details for a run |
| `GET` | `/flows/api/runs/{runId}/events` | Chronological run timeline (`?skip=`/`?take=`, default 200), derived from the run and its step attempts — no option needed |
| `GET` | `/flows/api/runs/{runId}/control` | Timeout, cancellation, idempotency state |
| `GET` | `/flows/api/runs/{runId}/lineage` | Re-run lineage: the source run this one was re-run from, plus the runs re-run from it |
| `POST` | `/flows/api/runs/{runId}/cancel` | Request cooperative cancellation |
| `POST` | `/flows/api/runs/{runId}/rerun` | Re-run a finished run with its original trigger payload (the idempotency header is stripped); the new run records `sourceRunId`. **`409`** with `runId: null, disabled: true` when the flow is disabled |
| `POST` | `/flows/api/runs/{runId}/steps/{stepKey}/retry` | Retry a failed step |

**Run search (`?search=`)** — how the term is applied, in order:

1. **A full run id** (GUID) is a primary-key lookup; it is not windowed.
2. **A status name** (`Failed`, `succeeded`), its display label (`Blocked`, `Timed Out`) or a common
   synonym (`Completed` → Succeeded, `Canceled` → Cancelled, `Timeout` → TimedOut, …) becomes the
   `status` filter, unless `?status=` is already set. The response's `search` object reports
   `kind: "status"`, `appliedStatus` and, for a synonym, `alias`; the SPA moves the term into the status
   dropdown and explains the mapping.
3. **Anything else** is a case-insensitive substring of the run's own columns — id, flow name, trigger
   key, status, background job id. Wrap the term in double quotes (`"Completed"`) to skip steps 1–2.

**Step payloads are searched only on request.** `?deep=true` (or `1`; the SPA's *Search step output /
errors* checkbox) also matches the run's current step rows — step key, error message, output JSON.
Superseded retry *attempt* history is never searched. `deep` defaults to `false`.

**Start-time window.** A text search without `?from=` is bounded to the last
`FlowDashboardOptions.RunSearchWindow` (7 days by default; anchored on `?to=` when only `to` is given).
`?from=` / `?to=` take ISO-8601 timestamps (no offset means UTC) and widen or narrow it. The response's
`search.from` / `search.windowDefaulted` say which window was used.

**Totals.** With `includeTotal=true` the response is
`{ items, total, hasMore, skip, take, search }`. A text or run-id search does not count its matches:
`total` is `null` and `hasMore` tells whether another page exists. Plain filters (flow, status, window)
return an exact `total`. `take` is clamped to 1–500 and a negative `skip` to 0.

Performance: every default search is an index seek or a range scan over the window on
`IX_FlowRuns_StartedAt`; see
[the production-shape benchmark](../benchmarks/run-search-production-shape-2026-10-31.md). The opt-in
payload search reads step payloads inside the window — on PostgreSQL the `pg_trgm` GIN indexes the
migrator creates best-effort accelerate it; on SQL Server it is a scan of the window's step rows.

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

### Servers

| Method | Path | Description |
|---|---|---|
| `GET` | `/flows/api/servers` | Every recorded server with `status` `Active` / `Stale`; `tracked: false` when the storage registers no `IFlowServerStore` |

```json
{
  "tracked": true,
  "heartbeatIntervalSeconds": 15,
  "staleAfterSeconds": 45,
  "serverTime": "2026-11-28T12:00:00+00:00",
  "servers": [
    {
      "serverId": "web-1:4242:0f8c2e6d9b1a4c7e8d3f5a6b7c8d9e0f",
      "machineName": "web-1",
      "processId": 4242,
      "runtime": "Hangfire",
      "version": "2.0.0",
      "startedAt": "2026-11-28T11:00:00+00:00",
      "lastHeartbeatAt": "2026-11-28T11:59:52+00:00",
      "lastSeenSeconds": 8,
      "activeStepCount": 3,
      "flowCount": 12,
      "status": "Active"
    }
  ]
}
```

A read API: viewers may call it. Brotli / Gzip per `Accept-Encoding`, like every dashboard JSON endpoint.

### Schedule Management

| Method | Path | Description |
|---|---|---|
| `GET` | `/flows/api/schedules` | List recurring cron jobs (runtime-agnostic via `IRecurringTriggerInspector`) |
| `POST` | `/flows/api/schedules/{jobId}/trigger` | Trigger a recurring job immediately |
| `POST` | `/flows/api/schedules/{jobId}/pause` | Pause a recurring job |
| `POST` | `/flows/api/schedules/{jobId}/resume` | Resume a paused recurring job |
| `PUT` | `/flows/api/schedules/{jobId}/cron` | Update the cron expression (`400` when it does not parse) |

---

## Retry a Failed Step

From the dashboard, open a failed run and click **Retry** on the failed step. This calls:

```http
POST /flows/api/runs/{runId}/steps/{stepKey}/retry
```

FlowOrchestrator resets the step (only a `Failed` step: anything else answers `400`), preserves all prior step outputs, and re-dispatches the step via the runtime adapter from the failure point. The response returns once the attempt is queued; the step runs on the runtime, not inside the request. Of two concurrent retries of the same step one is accepted and the other answers `409`. Steps that already succeeded are not re-executed.

## Cancel a Running Flow

```http
POST /flows/api/runs/{runId}/cancel
```

Sets a cancellation flag on the run's control record. Before dispatching or executing the next step the engine re-reads that record and, when `cancelRequested` is set, stops the run and marks it `Cancelled`. Cancellation is honoured at step boundaries — a step already in flight runs to completion.
