# Production Deployment Checklist

A pragmatic checklist for shipping FlowOrchestrator to production. Walk through every
section before go-live; bookmark this page for the first incident review.

For *changing* a flow once it is in production, see [Versioning Flows](versioning.md).

---

## 1. Storage and Persistence

FlowOrchestrator writes to twelve tables. The auto-migrator
(`FlowOrchestratorSqlMigrator`) creates them idempotently on startup.

| Table | Holds |
|---|---|
| `FlowDefinitions` | One row per registered flow (id, name, version, manifest JSON). |
| `FlowRuns` | One row per `TriggerAsync` invocation (status, trigger data, timestamps). |
| `FlowSteps` | One row per step instance per run (status, inputs, output). |
| `FlowStepAttempts` | Per-attempt history for retried steps (attempt number, error, durations). |
| `FlowOutputs` | Step outputs keyed by `(RunId, StepKey)`. |
| `FlowStepDispatches` | Idempotent dispatch ledger — supports the *Dispatch many, Execute once* invariant. |
| `FlowStepClaims` | Exclusive execution claim per step (the "Execute once" half). |
| `FlowRunControls` | Cancellation, idempotency key, run timeout. |
| `FlowIdempotencyKeys` | Idempotency dedupe at trigger time. |
| `FlowEvents` | State-transition audit log (when event persistence is enabled). |
| `FlowSignalWaiters` | Parked `WaitForSignal` steps. |
| `FlowScheduleStates` | Recurring-trigger sync state. |

> [!WARNING]
> **`UseInMemory()` is not for production.** All run data is stored in-process and
> lost on restart. The runtime is supported only for development and tests; the
> manifest's [Getting Started](getting-started.md#in-memory-dev--testing) page
> already calls this out, but it bears repeating here.

### SQL Server

- Use a **case-insensitive** collation (the default `SQL_Latin1_General_CP1_CI_AS` is fine). Step keys are matched as-is and a CS collation will surprise developers.
- Indexes are created by the auto-migrator. If you sharpen them, do it under a separate review — the engine relies on them for the dispatch-ledger fast path.
- Enable point-in-time recovery (PITR) — every table is part of the operational record and a 24 h gap is hard to reconstruct.

### PostgreSQL

- Tune `max_connections` against the Hangfire worker count (default 20) plus dashboard / API traffic.
- Leave autovacuum on. The dispatch ledger is hot under high throughput; bloat shows up as `EXPLAIN` plans falling off index scans.
- Logical replication or `pg_basebackup` for backups — both work, just have a tested restore drill.

### Backup recipe

Full backup + transaction-log / WAL replay covers every table above. Step inputs and outputs are persisted in `FlowOutputs`, so restoring is restoring **state**, not just configuration.

---

## 2. Multi-Instance Deployment

FlowOrchestrator's core invariant — **Dispatch many, Execute once** — is what makes
horizontal scale safe.

- `IFlowRunStore.TryRecordDispatchAsync` is an idempotent INSERT into `FlowStepDispatches`. Multiple workers may try; only one row lands. Backed by `SqlFlowRunStore` ([source](https://github.com/hoangsnowy/FlowOrchestrator/blob/main/src/FlowOrchestrator.SqlServer/SqlFlowRunStore.cs)).
- `TryClaimStepAsync` (the claim guard) ensures only one runtime instance executes a given step attempt; the others exit silently.
- **Cron triggers** under the Hangfire runtime use `IRecurringJobManager`, which fires each schedule on exactly one server. The ServiceBus runtime fires cron via self-perpetuating scheduled messages on a queue — Service Bus's exactly-once-per-tick delivery handles cross-replica coordination without leader election. No extra coordination needed in either case.

> [!WARNING]
> **InMemory runtime + multi-instance is not supported.** The dispatcher is a `Channel<T>` inside one process — instance B has no way to observe a job that instance A enqueued. Always combine `UseInMemoryRuntime()` with a single instance. For multi-replica deployments use `UseHangfire()` (with shared Hangfire storage) or `UseAzureServiceBusRuntime()` (with shared SB namespace).

> [!IMPORTANT]
> **Disabled flows (v1.22+).** Toggling `IsEnabled = false` on a flow record (via dashboard
> or API) silently rejects ALL trigger paths at the engine layer — manual, cron, webhook,
> re-trigger. Cron jobs are additionally pulled from the scheduler. In-flight runs are not
> cancelled; use the dashboard's Cancel run action for live work. Watch EventId 1010
> `TriggerRejectedDisabledFlow` to track how often disabled-flow triggers are still being
> attempted (a webhook producer or external cron not yet aware of the disable).

### Blue-green deployment

1. Pause every cron trigger (dashboard → trigger pause toggle).
2. Wait for `GET /flows/api/runs/active` to drain to `[]`.
3. Cut over traffic to the new version.
4. Resume cron triggers.

The dispatch ledger means an interrupted run will be re-driven by `FlowRunRecoveryHostedService` on the new instance — no manual replay step required.

---

## 3. Monitoring

### Logs

Every log line emitted by the engine includes the `RunId` and step key (when
applicable). Forward the following correlation fields if your aggregator strips
unknown ones:

- `RunId` (Guid)
- `FlowId` (Guid)
- `StepKey` (string)
- `Attempt` (int)

### OpenTelemetry traces and metrics

Enable via `options.Observability.EnableOpenTelemetry = true` and wire up via
`AddFlowOrchestratorInstrumentation()` — see [Observability](observability.md#opentelemetry).

| Span | Covers |
|---|---|
| `flow.trigger` | `TriggerAsync` from receipt to first step dispatch. |
| `flow.step` | One step end-to-end (expression resolution + handler). |
| `flow.step.poll` | One poll attempt of a `PollableStepHandler<T>`. |

| Metric | Type | Tags |
|---|---|---|
| `floworch.runs.started` | counter | `flow_id`, `trigger_type` |
| `floworch.runs.completed` | counter | `flow_id`, `status` |
| `floworch.steps.duration` | histogram (ms) | `flow_id`, `step_type` |
| `floworch.steps.poll_attempts` | histogram | `flow_id`, `step_key` |

### Suggested alerts

- **Stuck runs.** `count(active_runs WHERE status='Running' AND age > 1h) > 0` — usually a missing handler or external dependency timeout.
- **Failed-run rate.** `rate(floworch.runs.completed{status="failed"}[5m]) > 1` — tune to your volume.
- **Cron lag.** `now() - last_recurring_fire > 2 × schedule_interval` — Hangfire server unhealthy or paused inadvertently.
- **Queue depth.** Hangfire's own `enqueued` counter; FlowOrchestrator inherits it.

### Health-check endpoint

Wire up the bundled health check so the load balancer can drop traffic when storage is unreachable:

```csharp
builder.Services
    .AddHealthChecks()
    .AddFlowOrchestratorHealthChecks();   // probes IFlowStore reachability

var app = builder.Build();
app.MapHealthChecks("/health");
```

The check resolves `IFlowStore` from DI on every probe, so SQL Server, PostgreSQL,
and in-memory all work without re-registration. The probe budget defaults to 5
seconds and is configurable:

```csharp
.AddFlowOrchestratorHealthChecks(timeout: TimeSpan.FromSeconds(2));
```

Verify it:

```bash
curl -f https://app.example.com/health
# Healthy
```

```http
GET /health HTTP/1.1

HTTP/1.1 200 OK
Content-Type: text/plain

Healthy
```

The response degrades to `503 Service Unavailable` with body `Unhealthy` when the
flow store throws or the probe budget elapses — point your load balancer at it.

---

## 4. Secrets and Credentials

- **Connection strings.** Never in source. Use environment variables, `appsettings.{Environment}.json` outside of `main`, or a managed secret store.
- **Webhook secrets.** Each webhook trigger carries a `webhookSecret` input. Rotate it like any shared secret; supply a new value in the manifest and pause-resume the trigger so the routing table refreshes.
- **Dashboard Basic Auth.** The dashboard ships with optional Basic Auth — sufficient for an internal-only surface behind a VPN. **Insufficient for any production fronting the public internet** — put a real auth proxy (OIDC, Azure AD, Cloudflare Access) in front.
- **PII in step inputs/outputs.** Step inputs and outputs are persisted in `FlowSteps` and `FlowOutputs` and are visible in the dashboard's run detail view. **Treat them as logs.** If you need to ingest PII, redact before the step boundary and pass an opaque token instead.

---

## 5. Capacity Planning

### Storage cost per run

A single run typically writes:

- 1 row in `FlowRuns`
- *N* rows in `FlowSteps` (one per step)
- *N* rows in `FlowOutputs` (one per step that produced output)
- *M* rows in `FlowStepAttempts` (one per attempt — usually 1, more with retries)
- *K* rows in `FlowEvents` (when event persistence is on; ~5 per step)

For a 5-step flow with retries off and event persistence on, that is ~30 rows per run. At one million runs per month, expect tens of GB per year before retention.

### Retention

Turn it on:

```csharp
options.Retention.Enabled = true;
options.Retention.DataTtl = TimeSpan.FromDays(30);
options.Retention.SweepInterval = TimeSpan.FromHours(1);
```

Pick `DataTtl` against your audit requirements, not your storage budget — storage is cheap and a forensics request always wants a longer window than you set.

### Hangfire worker tuning

The default `WorkerCount = Environment.ProcessorCount * 5` is fine for most
FlowOrchestrator workloads (steps are typically I/O-bound). Increase only if
metrics show queue depth growing while CPU stays low. For background-CPU-bound
steps, lower it to avoid thrashing.

---

## 6. Upgrade Path

### Within a major version (1.x → 1.y)

`FlowOrchestratorSqlMigrator` runs on every startup and applies pending DDL idempotently. Releases note any table or column additions; rolling deploys are safe.

### Across major versions (1.x → 2.x)

A major bump may rename or split tables. The release notes include the migration
path; run it in a non-production environment first and verify with a smoke run
(see below).

### Disabling the auto-migrator

If your operations team owns DDL changes:

```csharp
options.UseSqlServer(connectionString, autoMigrate: false);
```

…then run the migration script (published in each release) under your own
deployment pipeline.

### Smoke-run verification

After any deploy:

1. Trigger a manual run of a no-op flow (the simplest one in your manifest).
2. Assert the run reaches `Status = Succeeded` within the SLA you expect.
3. Hit `/health` and check the response is `Healthy`.

If any of those fail, roll back before letting business traffic resume.

---

> [!TIP]
> Running FlowOrchestrator in production? Tell us about your deployment in the
> [GitHub Discussions](https://github.com/hoangsnowy/FlowOrchestrator/discussions)
> — patterns and pain points feed directly into the roadmap.
