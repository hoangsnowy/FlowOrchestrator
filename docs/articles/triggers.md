# Triggers

Triggers define how a flow is started. Each flow can declare any number of triggers in its manifest — all of them can fire independently to create new runs.

## Trigger Types

### Manual

The simplest trigger. A run is created when someone clicks **Trigger** in the dashboard or calls the REST API.

```csharp
["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
```

```http
POST /flows/api/flows/{flowId}/trigger
Content-Type: application/json

{ "orderId": "ORD-123" }
```

Any JSON body you include becomes the trigger payload, accessible in steps via `@triggerBody()`.

---

### Cron

Registers a recurring job with the active runtime adapter that fires the flow on a cron schedule.

- **Hangfire runtime** — maps to a Hangfire `RecurringJob` driven by `IRecurringJobManager`.
- **InMemory runtime** — driven by an in-process `PeriodicTimer` (1 s tick) using [Cronos](https://github.com/HangfireIO/Cronos) for expression parsing. No external scheduler needed.
- **ServiceBus runtime** (v1.22+) — schedules each cron firing as a Service Bus message with `ScheduledEnqueueTime` on the `flow-cron-triggers` queue. The cron consumer fires the trigger and self-perpetuates the next message. Single-delivery semantics across replicas — no leader election needed.
  Each job runs exactly one such chain. A chain is stamped with an epoch (its first tick), and the consumer
  drops a tick of an older chain, so a cron change, a restart or a resume replaces the chain instead of
  adding one next to it; a replica that receives a tick of a newer chain than it knows follows it. At startup
  the hub peeks the cron queue once and adopts the tick the previous process left instead of scheduling a
  second one. Before firing, the consumer also checks the shared state: a tick for a disabled flow, a trigger
  the manifest no longer declares, or (with `PersistOverrides`) a schedule paused on any replica is dropped.

All three runtimes implement the same `IRecurringTriggerDispatcher` / `IRecurringTriggerSync` abstractions, so cron behaviour (overrides, pause/resume, dashboard controls) is identical across them.

```csharp
["nightly"] = new TriggerMetadata
{
    Type = TriggerType.Cron,
    Inputs = new Dictionary<string, object?>
    {
        ["cronExpression"] = "0 2 * * *"  // every day at 02:00 UTC
    }
}
```

**Cron expression format:** 5-field cron (`min hour day month weekday`). A 6-field expression (`sec min hour day month weekday`) is also accepted and parsed with second precision; the InMemory runtime evaluates schedules on a 1 s tick, so sub-second granularity is not achievable.

Common patterns:

| Expression | Fires |
|---|---|
| `* * * * *` | Every minute |
| `*/5 * * * *` | Every 5 minutes |
| `0 9 * * 1-5` | Weekdays at 09:00 |
| `0 0 1 * *` | First day of every month |

> [!TIP]
> Cron overrides written via the dashboard or `PUT /flows/api/schedules/{jobId}/cron` are persisted when `Scheduler.PersistOverrides = true` and survive process restarts.
>
> The endpoint answers `400` for an expression that is not a valid 5-field (minute) or 6-field (second)
> cron expression, and nothing is persisted or rescheduled. An unparseable override already persisted by
> an older version is ignored at startup: the job runs on the manifest cron and a warning (EventId 5011)
> names it.

### Disabled flows

A flow whose `IFlowStore` record has `IsEnabled = false` (toggled via the dashboard's
disable button or `POST /flows/api/flows/{id}/disable`; re-enable with `POST /flows/api/flows/{id}/enable`)
starts no run on ANY trigger path — manual, cron, webhook, and re-trigger.

The engine consults `IFlowStore.GetByIdAsync(flowId).IsEnabled` at the top of `TriggerAsync` and
returns `new FlowTriggerResult(RunId: null, Disabled: true)` without dispatching, emitting EventId 1010
`TriggerRejectedDisabledFlow` and tagging the trigger activity with `flow.disabled = true`.

The HTTP layer reports that outcome rather than swallowing it:

| Endpoint | Status | Body |
|---|---|---|
| `POST /flows/api/flows/{id}/trigger` | `409 Conflict` | `{"runId":null,"disabled":true,"message":"…is disabled; no run was started."}` |
| `POST /flows/api/runs/{runId}/rerun` | `409 Conflict` | same, plus `sourceRunId` |
| `POST /flows/api/webhook/{slug}` | `403 Forbidden` | `{"error":"Flow is disabled."}` |

The webhook endpoint checks the flow record itself before reading the body, which is why it answers
`403` rather than `409`. If a flow is disabled in the narrow window *between* that check and the
engine call, the endpoint answers `202 Accepted` with `{"runId":null,"disabled":true,…}` — the
delivery is acknowledged as received while being reported as not started, because a webhook sender
cannot act on the distinction and retrying would not help.

> [!IMPORTANT]
> Before the fix for [#188](https://github.com/hoangsnowy/FlowOrchestrator/issues/188) these paths
> answered `200 OK` with a run id that no run backed — the endpoints generated the id before calling
> the engine and discarded its result. Reading that id returned `404`. If you have monitoring that
> treats a non-2xx trigger response as an incident, expect `409`s where you previously saw silent
> successes.

Cron is never affected by this at the HTTP layer: disabling a flow removes its recurring job, so no
tick is produced in the first place (see below).

Cron jobs are additionally removed from the scheduler when a flow is disabled, so cron ticks
don't even reach the engine in normal operation; the engine-level gate catches the narrow
race window where a tick fired just before the disable propagated. In-flight runs (those
already started before disable) are NOT cancelled — disable is for stopping NEW work, not
killing live work. To cancel a live run, use the dashboard's **Cancel run** action.

---

### Webhook

Registers a webhook endpoint. External systems `POST` to the URL to trigger a run.

```csharp
["order_received"] = new TriggerMetadata
{
    Type = TriggerType.Webhook,
    Inputs = new Dictionary<string, object?>
    {
        ["webhookSlug"]   = "new-order",       // endpoint: POST /flows/api/webhook/new-order
        ["webhookSecret"] = "my-secret-key"    // required in X-Webhook-Key header
    }
}
```

```http
POST /flows/api/webhook/new-order
Content-Type: application/json
X-Webhook-Key: my-secret-key

{ "orderId": "ORD-456", "total": 129.99 }
```

- `webhookSlug` — URL path segment. Must be unique across all registered webhooks.
- `webhookSecret` — When set, every incoming request must include `X-Webhook-Key: {secret}`. Requests without or with the wrong secret receive `401 Unauthorized`.
- If `webhookSecret` is omitted, the endpoint is unauthenticated.

### Enterprise hardening (v1.25)

The dashboard webhook endpoint runs a security pipeline: HMAC signature verification → IP allow / deny → rate limit → replay protection. Since 2.0 the default mode is `WebhookEnforcementMode.Enforce`: each gate a trigger declares rejects violating requests (a trigger that declares none is unaffected). Use `Audit` to dry-run a new publisher, or `Off` to disable the pipeline explicitly.

Activate the pipeline in DI:

```csharp
services.AddFlowDashboard(opts => opts.UseWebhookSecurity(sec =>
{
    sec.UseEnforcementMode(WebhookEnforcementMode.Enforce)
       .UseReplayProtection(toleranceSeconds: 300)
       .UseRateLimit(permitsPerSecond: 50, perIp: true)
       .UseForwardedHeaders(depth: 1)
       .UseMaxBodyBytes(1_048_576);
}));
```

**Both halves matter.** The DI call sets the mode and the host-wide limits; the manifest inputs below
configure *what* is enforced per trigger. `WebhookSecurityOptions.EnforcementMode` defaults to `Enforce`
since 2.0 (it was `Off` in 1.x, where a flow could declare `webhookHmacKey`,
`webhookReplayToleranceSeconds` and `webhookNonceHeader` and still have a bad signature, a replayed nonce
and a denied IP all sail through). Under `Audit` the pipeline evaluates every gate and records violations
but accepts the request; under `Off` it skips every gate. Check `/flows/api/webhooks/stats`: if violation
counters are climbing while requests still return `200`, you are in `Audit`; if they are flat while
invalid requests are accepted, you are in `Off`.

Note the asymmetry with the legacy `webhookSecret` / `X-Webhook-Key` check, which runs *outside* the
pipeline and therefore rejects with `401` whatever the enforcement mode is. `webhookSecret` secures an
endpoint on its own; `webhookHmacKey` does not.

Configure per flow via manifest inputs. The full set of v1.25 fields:

| Field | Purpose |
|-------|---------|
| `webhookSignatureScheme` | Selects a built-in dialect: `GitHub`, `GitHubLegacy`, `Bitbucket`, `Stripe`, `Slack`, `Shopify`, `Twilio`, `Square`, `Zoom`, `Linear`, `Dropbox`, `Mailgun`, `MicrosoftTeams`, `Atlassian`, `Calendly`, `Generic`, or `Custom`. |
| `webhookHmacKey` | Signing key used for HMAC verification. Falls back to `webhookSecret` when absent. |
| `webhookHmacKeyPrevious` / `webhookSecretPrevious` | Optional rotated-out key. Successful matches against the previous key emit `EventId 4010` (`WebhookLog.RotationUsedPreviousKey`). |
| `webhookSignatureHeader`, `webhookSignatureAlgorithm`, `webhookSignatureEncoding`, `webhookSignaturePrefix`, `webhookSignatureMultiValueDelimiter`, `webhookSignatureKeyValueSeparator`, `webhookSignatureValueKey`, `webhookTimestampValueKey`, `webhookTimestampHeader`, `webhookSignedPayloadStrategy`, `webhookSignedPayloadDelimiter`, `webhookSignedPayloadVersion`, `webhookHeaderValuePrefix`, `webhookCustomStrategyName` | Drive the `Custom` scheme directly from the manifest (every aspect of the wire format is controllable). |
| `webhookReplayToleranceSeconds` | Max clock skew between publisher timestamp and server time. `0` disables replay protection. |
| `webhookNonceHeader` | Optional explicit nonce header (e.g. `X-GitHub-Delivery`). Default: SHA-256 of `timestamp \|\| body`. |
| `webhookRateLimitPermitsPerSecond`, `webhookRateLimitBurstSize`, `webhookRateLimitPerIp` | Token-bucket overrides. Per-IP keying is opt-in. |
| `webhookIpAllowList`, `webhookIpDenyList` | IP entries — CIDR (`10.0.0.0/8`, `2001:db8::/32`), inclusive range (`10.0.0.10-10.0.0.42`), wildcard (`10.0.*.*`), single address, or any combination. Accepts an array OR a comma-delimited string. Allow takes precedence. |
| `webhookIpAllowListPreset` | Single curated publisher CIDR set. Presets: `github`, `stripe`, `shopify`, `twilio`, `square`, `atlassian`, `bitbucket`, `slack`, `mailgun`, `zoom`, `local` / `private`. |
| `webhookIpAllowListPresets` | Multiple presets combined (array or comma-delimited string). Merges with `webhookIpAllowListPreset` and the explicit `webhookIpAllowList`. |

Per-publisher cookbook example — GitHub:

```csharp
["webhook"] = new TriggerMetadata
{
    Type = TriggerType.Webhook,
    Inputs = new Dictionary<string, object?>
    {
        ["webhookSlug"] = "github-events",
        ["webhookHmacKey"] = "<the secret you configured at github.com/.../settings/hooks>",
        ["webhookSignatureScheme"] = "GitHub",
        ["webhookReplayToleranceSeconds"] = 300,
        ["webhookNonceHeader"] = "X-GitHub-Delivery",
        ["webhookIpAllowListPreset"] = "github",
    }
}
```

The dashboard exposes a "Webhooks" tab that surfaces the recent-deliveries log and a 24-hour reason histogram (`GET /flows/api/webhooks/recent`, `GET /flows/api/webhooks/stats`). Rejected requests are persisted to the in-memory ring buffer (last 1 000 entries by default). For multi-replica deployments or long retention, swap the in-memory stores for a durable backend with `options.AddSqlServerWebhookHardening(conn)` or `options.AddPostgreSqlWebhookHardening(conn)` — see [Storage](storage.md).

---

## Multiple Triggers per Flow

A single flow can declare any combination of triggers:

```csharp
Triggers = new FlowTriggerCollection
{
    ["manual"]   = new TriggerMetadata { Type = TriggerType.Manual },
    ["nightly"]  = new TriggerMetadata
    {
        Type = TriggerType.Cron,
        Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 1 * * *" }
    },
    ["webhook"]  = new TriggerMetadata
    {
        Type = TriggerType.Webhook,
        Inputs = new Dictionary<string, object?>
        {
            ["webhookSlug"]   = "process-batch",
            ["webhookSecret"] = "batch-secret"
        }
    }
}
```

Each trigger type fires independently and creates a separate run.

---

## Idempotency

To prevent duplicate runs when the same event is delivered more than once (at-least-once delivery from upstream systems):

```http
POST /flows/api/flows/{id}/trigger
Content-Type: application/json
Idempotency-Key: batch-2026-04-19-001

{ "batchId": "BATCH-001" }
```

If a run with the same `Idempotency-Key` value already exists for this flow **and the same trigger**, FlowOrchestrator returns the existing `runId` without creating a new run.

The header name is configurable:

```csharp
options.RunControl.IdempotencyHeaderName = "Idempotency-Key";  // default
```

Idempotency keys work for both manual triggers and webhook triggers, but they are scoped per (flow, trigger key): the same key delivered to a flow's manual trigger and to its webhook trigger creates two separate runs.

### Per-run timeout override

Include the reserved `runTimeoutSeconds` key in the trigger JSON body to override `options.RunControl.DefaultRunTimeout` for that run only:

```http
POST /flows/api/flows/{id}/trigger
Content-Type: application/json

{ "batchId": "BATCH-001", "runTimeoutSeconds": 900 }
```

The value must be a positive integer (a numeric string is also accepted when the payload is a dictionary); anything else is ignored and the global default applies. Once the deadline passes the run is marked `TimedOut` — lazily when a step is dispatched after the deadline, and proactively by the sweep governed by `options.RunControl.TimeoutEnforcementInterval`.

---

## Trigger Payload Access

The JSON body and headers of the triggering request are persisted and available in any step's inputs via expressions:

```csharp
// In StepMetadata.Inputs:
["orderId"]    = "@triggerBody()?.orderId"
["requestId"]  = "@triggerHeaders()['X-Request-Id']"
["allOrders"]  = "@triggerBody()?.orders"
```

See [Expressions](expressions.md) for the full expression reference.
