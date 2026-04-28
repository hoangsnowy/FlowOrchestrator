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

Registers a recurring job with the active runtime adapter that fires the flow on a cron schedule. When using the Hangfire runtime, this maps to a Hangfire `RecurringJob`.

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

**Cron expression format:** standard 5-field Quartz/Hangfire cron (`min hour day month weekday`).

Common patterns:

| Expression | Fires |
|---|---|
| `* * * * *` | Every minute |
| `*/5 * * * *` | Every 5 minutes |
| `0 9 * * 1-5` | Weekdays at 09:00 |
| `0 0 1 * *` | First day of every month |

> [!TIP]
> Cron overrides written via the dashboard or `PUT /flows/api/schedules/{jobId}/cron` are persisted when `Scheduler.PersistOverrides = true` and survive process restarts.

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

If a run with the same `Idempotency-Key` value already exists for this flow, FlowOrchestrator returns the existing `runId` without creating a new run.

The header name is configurable:

```csharp
options.RunControl.IdempotencyHeaderName = "Idempotency-Key";  // default
```

Idempotency keys work for both manual triggers and webhook triggers.

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
