# `WaitForSignal` Step — Human-in-the-Loop Workflows

The built-in `WaitForSignal` step parks a flow indefinitely until an external HTTP signal is delivered. Use it for approval workflows, content moderation gates, manual QA sign-off, or any "pause until a human (or external system) says go" pattern — without burning a worker thread on a polling loop.

It is the smallest possible primitive that unlocks the entire **approval workflow** category: ship value in a weekend, not a quarter.

## When To Use It vs. Polling

| Pattern | Use case | Mechanism |
|---|---|---|
| `PollableStepHandler<T>` | Wait for an external system that *will* eventually respond on its own (job status, file appearance, blob upload completion). | Step re-runs on a fixed cadence; you write the fetch + condition. |
| `WaitForSignal` | Wait for an external system or human that will *push* a notification when ready (manager approval, webhook from third party, async batch completion). | Step parks; an HTTP POST wakes it up. |

Rule of thumb: if you'd hit a rate limit polling once a minute, `WaitForSignal` is the right choice.

## Manifest

```csharp
["wait_for_approval"] = new StepMetadata
{
    Type = "WaitForSignal",
    RunAfter = new RunAfterCollection { ["submit_request"] = [StepStatus.Succeeded] },
    Inputs = new Dictionary<string, object?>
    {
        ["signalName"]     = "approval",
        ["timeoutSeconds"] = 86400        // optional; null/omitted = wait indefinitely
    }
}
```

Fields:

- `signalName` (required) — the logical name addressed by the signal endpoint. Must be unique among the parked `WaitForSignal` steps in the same run.
- `timeoutSeconds` (optional) — absolute deadline. When elapsed without delivery, the step transitions to `Failed` with a descriptive reason. `null` or non-positive values mean "wait forever".

## End-to-End Example

```csharp
public sealed class ApprovalFlow : IFlowDefinition
{
    public Guid Id { get; } = new("00000000-0000-0000-0000-000000000007");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["submit"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new() { ["message"] = "Awaiting manager approval" }
            },
            ["wait_for_approval"] = new StepMetadata
            {
                Type = "WaitForSignal",
                RunAfter = new() { ["submit"] = [StepStatus.Succeeded] },
                Inputs = new()
                {
                    ["signalName"]     = "approval",
                    ["timeoutSeconds"] = 86400
                }
            },
            ["finalize"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new() { ["wait_for_approval"] = [StepStatus.Succeeded] },
                Inputs = new()
                {
                    ["message"] = "@steps('wait_for_approval').output.approver"
                }
            }
        }
    };
}
```

Wake the parked step from `curl`:

```bash
curl -X POST http://localhost:5000/flows/api/runs/<runId>/signals/approval \
     -H "Content-Type: application/json" \
     -d '{"approver":"alice@example.com","approved":true}'
```

Response on success:

```json
{
  "delivered": true,
  "stepKey": "wait_for_approval",
  "deliveredAt": "2026-05-01T14:33:21+00:00"
}
```

Downstream steps consume the payload via the same expression syntax used for any step output:

```csharp
["message"] = "@steps('wait_for_approval').output.approver"
```

## Endpoint Status Codes

| Status | Meaning |
|---|---|
| `200` | Signal delivered. Body includes `stepKey` and `deliveredAt`. |
| `404` | Run does not exist, or no waiter is registered for that signal name on the run. |
| `409` | A signal has already been delivered for this waiter — second delivery rejected. |
| `400` | Body is not valid JSON, or run is no longer in `Running` status. |

## Timeout Patterns

Set `timeoutSeconds` to fail the step (and therefore the run, unless you wire a recovery branch with a downstream step that runs on `[StepStatus.Failed]`) after a deadline:

```csharp
["wait_for_approval"] = new StepMetadata
{
    Type = "WaitForSignal",
    RunAfter = new() { ["submit"] = [StepStatus.Succeeded] },
    Inputs = new()
    {
        ["signalName"]     = "approval",
        ["timeoutSeconds"] = 3600   // 1 hour
    }
},
["escalate_to_director"] = new StepMetadata
{
    Type = "NotifyDirector",
    RunAfter = new() { ["wait_for_approval"] = [StepStatus.Failed] }
}
```

The step's `FailedReason` reads `Signal '<name>' not received within <n>s.` so it is easy to distinguish from a handler exception in the dashboard.

## Multi-Signal Patterns

A run may have multiple `WaitForSignal` steps, each with its own `signalName`. They can run in parallel (no `runAfter` between them) or in sequence.

**Both manager and finance must approve:** two parallel waiters, both must succeed:

```csharp
["wait_manager"] = new StepMetadata
{
    Type = "WaitForSignal",
    Inputs = new() { ["signalName"] = "manager-approval" }
},
["wait_finance"] = new StepMetadata
{
    Type = "WaitForSignal",
    Inputs = new() { ["signalName"] = "finance-approval" }
},
["finalize"] = new StepMetadata
{
    Type = "Echo",
    RunAfter = new()
    {
        ["wait_manager"] = [StepStatus.Succeeded],
        ["wait_finance"] = [StepStatus.Succeeded]
    }
}
```

Manager and finance each POST to their respective signal name; only when both have arrived does `finalize` run.

## Security

The signal endpoint accepts any JSON body and is **not** authenticated by default. Production deployments should:

1. Wrap the dashboard route group in your standard auth middleware (`UseAuthentication()` / `UseAuthorization()` before `MapFlowDashboard()`).
2. Or front the dashboard with API gateway / mTLS / service-mesh policy that verifies the caller has authority to deliver signals.

The endpoint enforces only one structural check beyond your middleware: the run must be in `Running` status. A delivered signal cannot resurrect a cancelled or completed run.

## Observability

When event persistence is enabled (`builder.Observability.EnableEventPersistence = true`), the engine emits these events for each `WaitForSignal` step:

- `step.started` on every invocation (initial park, signal arrival, timeout fire).
- `step.pending` after the first invocation parks the step.
- `step.completed` on successful delivery.
- `step.failed` on timeout.

The dashboard run detail page shows the events in chronological order, plus the resolved input/output and any handler logs surfaced through `IExecutionContext`.

## Restart Safety

Waiters live in your configured storage (`FlowSignalWaiters` table for SQL Server, `flow_signal_waiters` for PostgreSQL, `ConcurrentDictionary` for in-memory). A process restart does not lose pending waits: when the worker comes back up, `FlowRunRecoveryHostedService` restores active runs, and the next invocation of the parked step picks up the persisted waiter row. In-memory storage is lost on restart, by design.

## Out of Scope

For deliberate simplicity, this primitive does **not** include:

- HMAC-signed signal endpoints. Use auth middleware instead.
- Broadcast multi-recipient signals. Each waiter is identified by `(RunId, StepKey)`; one signal targets one waiter.
- Signal cancellation API. Cancel the run instead.
- Long-poll API for callers waiting for delivery confirmation.
- Persistent signal queues for signals that arrive before a step is ready to receive them.

If you find yourself needing one of these, you've outgrown the primitive — consider modeling the workflow as a separate service that triggers FlowOrchestrator, rather than driving FlowOrchestrator from the wait point.
