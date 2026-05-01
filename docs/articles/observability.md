# Observability

FlowOrchestrator exposes run events, OpenTelemetry traces/metrics, and a retention system to give you visibility into what your flows are doing in production.

## Run Events

When event persistence is enabled, FlowOrchestrator writes structured `FlowEvent` records for every state transition: run started, step queued, step started, step completed/failed, run completed. Built-in step types — including `WaitForSignal` (`step.pending` while waiting, `step.completed` once the signal lands) and `ForEach` (per child-iteration events) — emit through the same channel as user-written handlers.

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
using FlowOrchestrator.Core.Observability;

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

`AddFlowOrchestratorInstrumentation()` is an extension method on both `TracerProviderBuilder` and `MeterProviderBuilder`. It subscribes to the `FlowOrchestrator` activity source and meter, and now lives in `FlowOrchestrator.Core.Observability` (moved from `FlowOrchestrator.Hangfire` in v1.19). The Hangfire namespace still exposes `[Obsolete]` shims for one release so existing code keeps compiling.

### What is emitted

**Traces (every span is on the `FlowOrchestrator` activity source):**

| Span | Kind | When | Notable tags |
|---|---|---|---|
| `flow.trigger` | Internal | One per `TriggerAsync` call | `flow.id`, `run.id`, `trigger.key`, `trigger.type`, `duplicate` (set when idempotency dedupe fires) |
| `flow.step` | Internal | One per `RunStepAsync` call | `flow.id`, `run.id`, `step.key`, `step.type` |
| `flow.step.retry` | Internal | One per `RetryStepAsync` call | `flow.id`, `run.id`, `step.key` |
| `flow.step.when` | Internal | When a step's `When` clause is evaluated | `flow.id`, `run.id`, `step.key`, `flow.when.expression`, `flow.when.resolved`, `flow.when.result` |
| `flow.step.poll` | Internal | One per polling iteration in `PollableStepHandler` | `flow.id`, `run.id`, `step.key`, `flow.poll.attempt`, `flow.poll.condition_met` |
| `flow.runtime.execute` | Consumer | Wraps each Hangfire job. Restores the parent `traceparent` captured at enqueue, so step spans become children of the original caller. | `messaging.system=hangfire`, `messaging.message.id` |
| `flow.webhook.receive` | Server | One per inbound webhook hit, parented onto the caller's `traceparent` header | `flow.webhook.slug_or_id` |
| `flow.signal.deliver` | Server | One per inbound signal HTTP call, parented onto the caller's `traceparent` | `flow.run_id`, `flow.signal_name` |

Failures set `Status = Error` on the activity and add an `exception` event with the standard
OTel tags (`exception.type`, `exception.message`, `exception.stacktrace`). APMs treat the span as
red without any extra configuration.

**Metrics (every instrument is on the `FlowOrchestrator` meter):**

| Metric | Type | Unit | Tags |
|---|---|---|---|
| `flow_runs_started` | counter | runs | `flow_id`, `trigger_key` |
| `flow_runs_completed` | counter | runs | `status` |
| `flow_steps_completed` | counter | steps | `flow_id`, `status` |
| `flow_step_duration_ms` | histogram | ms | `flow_id`, `step_key`, `status` |
| `flow_step_queue_delay_ms` | histogram | ms | `flow_id`, `step_key` |
| `flow_step_retries` | counter | retries | `flow_id`, `step_key` |
| `flow_step_skipped` | counter | steps | `flow_id`, `step_key`, `reason` (`when_false` / `prerequisites_unmet`) |
| `flow_step_poll_attempts` | counter | attempts | `flow_id`, `step_key` |
| `flow_signal_wait_ms` | histogram | ms | `flow_id`, `step_key`, `signal_name` — recorded by `FlowSignalDispatcher` on delivery |
| `flow_cron_lag_ms` | histogram | ms | `flow_id`, `trigger_key`, `runtime` (`hangfire` / `in_memory`) — gap between scheduled fire and actual dispatch |

### Distributed tracing across the runtime

A single `traceId` connects everything from the inbound HTTP request to the last step's exit:

```
caller traceparent
   └── flow.webhook.receive          (Dashboard, Server)
         └── flow.trigger             (engine)
               └── flow.runtime.execute  (Hangfire, Consumer)
                     └── flow.step       (engine, per dispatched step)
                           └── flow.step.poll  (handler, per poll attempt)
```

The `flow.runtime.execute` wrapper is opened by `TraceContextHangfireFilter` (registered automatically when `options.UseHangfire()` is set). It captures `Activity.Current.Context` on enqueue, persists the W3C identifiers as Hangfire job parameters, and restores them as the parent context when the worker picks the job up. Inbound webhook and signal endpoints in the Dashboard read the `traceparent` / `tracestate` headers via `InboundTraceContext` and start their entry-point activity as a child of the parsed context.

Without this plumbing, a Hangfire-backed run would appear as a forest of disconnected root spans — one per step. With it, an APM shows a single connected tree from the original caller down to every step.

### Sampling

OTel sampling for traces should be configured at the SDK, not at FlowOrchestrator. For low-volume systems start with `AlwaysOnSampler`; for high-volume production prefer parent-based sampling so trace continuity is preserved across the runtime boundary:

```csharp
.WithTracing(t => t
    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.05))) // 5% root sampling
    .AddFlowOrchestratorInstrumentation()
    .AddOtlpExporter());
```

Metrics are aggregated, not sampled — leave the default cardinality limits in place and only override if you see exporter overflow warnings.

### Logger scopes and EventIds

The engine wraps every public entry point (`TriggerAsync`, `RunStepAsync`, `RetryStepAsync`) in `_logger.BeginScope(...)` carrying `RunId`, `FlowId`, `StepKey`, and `Attempt` (when applicable). Every nested log line — including logs from your own step handlers — carries those properties automatically. Logging providers that honour scopes (Serilog, NLog, OpenTelemetry Logs, Application Insights, Datadog, …) surface them as searchable fields. Engine hot-path log calls go through source-generated `[LoggerMessage]` partial methods (`EngineLog.cs`) for zero-allocation, AOT-friendly emission. See [Logging integrations](#logging-integrations) below for concrete provider wireup.

Stable `EventId` constants are defined in `FlowOrchestrator.Core.Observability.LogEvents` so production users can filter or alert on a specific log event without parsing the message template:

```csharp
LogEvents.RunStarted        = 1000
LogEvents.RunCompleted      = 1001
LogEvents.StepStarted       = 2000
LogEvents.StepCompleted     = 2001
LogEvents.StepFailed        = 2002
LogEvents.StepSkipped       = 2003
LogEvents.WhenEvaluationFailed = 2005
LogEvents.DispatchEnqueued  = 3000
// …see the source for the full list.
```

### Logging integrations

The library is logging-framework-agnostic — it only uses `Microsoft.Extensions.Logging.ILogger<T>`. Plug in any provider that honours `ILogger.BeginScope` and you get the engine's correlation properties (`RunId`, `FlowId`, `StepKey`, `Attempt`) on every nested log line, including logs emitted by your own step handlers.

#### Microsoft.Extensions.Logging (Console)

Built into the framework. Scopes are off by default — opt in via the formatter options:

```csharp
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
// or
builder.Logging.AddSimpleConsole(o => o.IncludeScopes = true);
```

Output:

```json
{ "Timestamp":"…", "EventId":2002, "LogLevel":"Error",
  "Category":"FlowOrchestrator.Core.Execution.FlowOrchestratorEngine",
  "Message":"Step execution failed for submit_to_wms",
  "Scopes":[{"RunId":"3fa85f64-…","FlowId":"a1b2c3d4-…","StepKey":"submit_to_wms"}] }
```

#### Serilog

```bash
dotnet add package Serilog.AspNetCore
dotnet add package Serilog.Sinks.Seq      # or Console / File / Datadog / Splunk / …
```

```csharp
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()                        // <-- required so scope props become structured fields
    .Enrich.WithProperty("Service", "OrderHub")
    .WriteTo.Seq("http://seq:5341"));
```

Query in Seq / any structured sink:

```
EventId.Id = 2002 and StepKey = 'submit_to_wms'
```

#### NLog

```bash
dotnet add package NLog.Web.AspNetCore
```

```csharp
builder.Host.UseNLog();
```

`nlog.config` — use `${scopeproperty}` to render scope keys:

```xml
<targets>
  <target xsi:type="Console" name="console"
          layout="${longdate} ${level} ${event-properties:item=EventId_Id} run=${scopeproperty:item=RunId} step=${scopeproperty:item=StepKey} - ${message} ${exception:format=tostring}" />
</targets>
```

#### OpenTelemetry Logs (auto trace correlation)

When you already use OTel for traces, exporting logs through the same pipeline gives automatic `TraceId` / `SpanId` correlation in every log line — clicking a log in your APM jumps straight to the trace:

```csharp
using FlowOrchestrator.Core.Observability;

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;                         // <-- emits RunId/FlowId/StepKey as log attributes
    o.ParseStateValues = true;
    o.AddOtlpExporter();
});

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddFlowOrchestratorInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddFlowOrchestratorInstrumentation().AddOtlpExporter());
```

#### Application Insights / Datadog / Splunk / Seq / Loki

All honour `BeginScope` — scope properties surface as `customDimensions` (App Insights), structured tags (Datadog/Splunk/Loki), or top-level fields (Seq) automatically. No FlowOrchestrator-specific configuration required beyond enabling scopes on the provider.

> [!TIP]
> If your scope properties are missing in the sink output, the provider almost certainly has scopes disabled by default. Search its docs for `IncludeScopes` (Microsoft.Extensions.Logging.Console, OpenTelemetry), `Enrich.FromLogContext` (Serilog), or `${scopeproperty}` (NLog).

### Health checks

Wire the bundled storage probe so a load balancer can drop traffic when the flow store is unreachable:

```csharp
builder.Services.AddHealthChecks().AddFlowOrchestratorHealthChecks();
app.MapHealthChecks("/health");
```

The check resolves whichever `IFlowStore` you registered (SQL Server, PostgreSQL, in-memory). Probe budget defaults to 5 s and is configurable. See [Production Checklist](production-checklist.md#3-monitoring) for the full operational story.

### Running with .NET Aspire

When running under Aspire, `OTEL_EXPORTER_OTLP_ENDPOINT` is injected automatically. Spans and metrics appear in the Aspire Dashboard with no extra configuration beyond `AddFlowOrchestratorInstrumentation()`.

> [!IMPORTANT]
> **For the engine's structured logs to show up in Aspire's Logs tab** (with `RunId` / `StepKey` / `EventId` as searchable attributes), wire up `builder.Logging.AddOpenTelemetry(...)` with `IncludeScopes = true` and `AddOtlpExporter()` — see the [OpenTelemetry Logs](#opentelemetry-logs-auto-trace-correlation) example above. OTel's tracing and metrics pipelines do *not* automatically wire the logging pipeline; without this snippet the Logs tab will be empty even though traces and metrics flow through.

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
