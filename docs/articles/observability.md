# Observability

FlowOrchestrator exposes run events, OpenTelemetry traces/metrics, and a retention system to give you visibility into what your flows are doing in production.

## Run Events

When event persistence is enabled, FlowOrchestrator writes structured `FlowEvent` records for every state transition: run started, step queued, step started, step completed/failed, run completed.

```csharp
options.Observability.EnableEventPersistence = true;
```

Events are queryable via the REST API:

```http
GET /flows/api/runs/{runId}/events
```

Returns a time-ordered list of events with timestamps and payload. These power the step timeline view in the dashboard.

### Without event persistence

The dashboard can still show step status and I/O from `FlowSteps` and `FlowOutputs`, but the precise timing of each state transition is unavailable.

---

## OpenTelemetry

When enabled, FlowOrchestrator registers an `ActivitySource` and an `IMeterFactory`-backed `Meter` that emit spans and metrics compatible with any OTLP-compatible backend (Jaeger, Grafana Tempo, Azure Monitor, Aspire Dashboard).

```csharp
options.Observability.EnableOpenTelemetry = true;
```

Wire up the instrumentation in your OTel pipeline:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("MyApp"))
    .WithTracing(t => t
        .AddFlowOrchestratorInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddFlowOrchestratorInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
```

`AddFlowOrchestratorInstrumentation()` is an extension method on both `TracerProviderBuilder` and `MeterProviderBuilder`. It subscribes to the `FlowOrchestrator` activity source and meter.

### What is emitted

**Traces:**
- `flow.trigger` span — covers `TriggerAsync` from trigger receipt to first Hangfire enqueue
- `flow.step` span — covers the full execution of each step, including expression resolution and handler dispatch
- `flow.step.poll` span — each individual poll attempt for polling steps

**Metrics:**
- `floworch.runs.started` — counter, tagged with `flow_id`, `trigger_type`
- `floworch.runs.completed` — counter, tagged with `flow_id`, `status` (`succeeded`/`failed`/`cancelled`/`timed_out`)
- `floworch.steps.duration` — histogram (milliseconds), tagged with `flow_id`, `step_type`
- `floworch.steps.poll_attempts` — histogram, tagged with `flow_id`, `step_key`

### Running with .NET Aspire

When running under Aspire, `OTEL_EXPORTER_OTLP_ENDPOINT` is injected automatically. Spans and metrics appear in the Aspire Dashboard with no extra configuration beyond `AddFlowOrchestratorInstrumentation()`.

---

## Run Control State

```http
GET /flows/api/runs/{runId}/control
```

Returns the current control record for a run:

```json
{
  "runId": "...",
  "cancellationRequested": false,
  "timedOutAt": null,
  "idempotencyKey": "batch-2026-04-20-001",
  "timeoutAt": "2026-04-20T12:10:00Z"
}
```

This is useful for diagnosing why a run stopped or was cancelled.

---

## Active Runs

```http
GET /flows/api/runs/active
```

Returns all runs currently in `Running` status. Use this to build operational monitors or alert on stuck runs.

---

## Dashboard Statistics

```http
GET /flows/api/runs/stats
```

```json
{
  "totalFlows": 6,
  "activeRuns": 2,
  "succeededToday": 47,
  "failedToday": 1,
  "cancelledToday": 0
}
```

---

## Data Retention

FlowOrchestrator can automatically delete old run data to prevent unbounded database growth.

```csharp
options.Retention.Enabled = true;
options.Retention.DataTtl = TimeSpan.FromDays(30);     // delete runs older than 30 days
options.Retention.SweepInterval = TimeSpan.FromHours(1); // run the sweep every hour
```

When enabled, `FlowRetentionHostedService` runs on the configured interval and calls `IFlowRetentionStore.DeleteOldRunsAsync(cutoff)`. The SQL Server and PostgreSQL backends cascade-delete all related records (steps, outputs, events, control) when a run is deleted.

> [!TIP]
> `SweepInterval` defaults to 1 hour and `DataTtl` defaults to 30 days. Retention is disabled by default — opt in explicitly.

| Option | Default | Description |
|---|---|---|
| `Retention.Enabled` | `false` | Enable the background sweep |
| `Retention.DataTtl` | 30 days | Runs older than this threshold are deleted |
| `Retention.SweepInterval` | 1 hour | How often the sweep runs |
