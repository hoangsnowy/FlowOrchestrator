# Versioning Flows in Production

Once a flow has run in production, its definition is no longer a free-form artifact —
every change must be reasoned about against in-flight runs, the run history, and the
recurring-job state stored by the runtime. This page states exactly what an in-flight run
does when you deploy a changed manifest, and the rule-set for changing a deployed flow safely.

## The Mental Model — Three Layers of Version

FlowOrchestrator separates *identity*, *display*, and *shape*:

| Layer | Type | Mutability | Effect of changing |
|---|---|---|---|
| **Flow `Id`** | `Guid` | **Immutable** — never change after first deploy. | All historical runs, recurring jobs, dispatch ledger entries, and webhook routes are keyed by `Id`. Changing it orphans every existing record and creates a new flow. |
| **Flow `Version` string** | `string` | Free-form display label. | Cosmetic only. Bump it whenever you ship a meaningful change so the dashboard and run history make the timeline legible. The engine does not read it. |
| **Manifest shape** | `FlowManifest` | Mutable, but each change has consequences. | The actual graph — triggers, steps, `RunAfter`, `When`, `Inputs`. The rest of this page is about what a change does to runs that are already in flight. |

> [!IMPORTANT]
> The `Id` is stored in the database and used to route Hangfire jobs and webhook callbacks across all runtimes. **Never change it** — see [Getting Started](getting-started.md#define-your-first-flow).

---

## The Rule — In-Flight Runs Follow the Latest Manifest

> [!IMPORTANT]
> **There is no per-run manifest pinning.** A run does not remember the manifest it was started
> under. Every time the engine decides what a run does next — the continuation after each step
> completes, startup recovery, a signal resume — it evaluates the run's recorded step statuses
> against the manifest **the deciding worker has loaded now**. Runs started before a deploy
> continue under the new graph from the first decision taken after it.

Concretely, from that moment on:

- **A step of the new manifest whose `runAfter` prerequisites are satisfied by the run's recorded
  statuses is ready, and it runs** — even when it was added after those prerequisites completed,
  and even when steps that were meant to follow it have already run. A step with no `runAfter`
  (an entry step) is always satisfied, so a **new entry step runs in every in-flight run**.
- A step whose prerequisites finished in a status its `runAfter` does not accept is recorded
  `Skipped`, exactly as for a new run. `When` clauses are evaluated when the step is scheduled,
  against the new manifest.
- **A step that is no longer in the manifest is never dispatched again.** Its recorded rows stay
  in the run's history. If it was already queued when the deploy landed, the worker that picks it
  up records it `Skipped` (reason `Step metadata not found.`) without running the handler, and
  the run moves on.
- Recorded statuses are never re-evaluated: a step that already ran is not re-run, and a step that
  was already recorded `Skipped` stays skipped, even if the new manifest would decide differently.

The same change produces the same outcome on every runtime (Hangfire, in-memory, Service Bus) and
on every path — the next step's continuation, startup recovery, or a recovery that redelivers the
in-memory runtime's lost queue.

### What each kind of change does to a run that is in flight

The run below was parked at a `WaitForSignal` step `wait` of the v1 graph `s1 → wait → s3 → s4`
when v2 was deployed, and was signalled afterwards. This is the shape the stress harness's
`deploy-mid-run` scenario exercises, and every row is locked by a unit test
(`InFlightManifestChangeTests`).

| v2 change | What the in-flight run does |
|---|---|
| **Append** `s5` after `s4` | Runs `s5` after `s4`. Ends `Succeeded`. |
| **Insert** `mid` between `wait` and `s3` (`s3` now runs after `mid`) | Runs `mid`, then `s3`, then `s4`. A run that had *already passed* `wait` when the deploy landed also runs `mid`, on the next decision taken for it, because its prerequisite is met — even though `s3` may already have run. |
| **Rename** `s3` → `s3b` | Runs `s3b` and `s4`; `s3` never runs. If `s3` was already queued, it is recorded `Skipped` and `s3b` runs. |
| **Remove** `s3` (`s4` now runs after `wait`) | Runs `s4` after `wait`. If `s3` was already queued, it is recorded `Skipped`. |
| **New `runAfter`**: `side` after the already-completed `s1`; `s4` after `s3` + `side` | Runs `side` straight away (its prerequisite completed before it existed), then `s4` once both `s3` and `side` succeeded. Ends `Succeeded`. |
| **New entry step** `e0` | Runs `e0` in the in-flight run. Ends `Succeeded`. |
| **New or changed `When`** on an edge | Applied to any step scheduled after the deploy; steps already scheduled or recorded are not re-judged. |
| **Changed `Type`** of a step | A step that starts on a worker running the new manifest runs the new handler. |
| **Changed `Inputs`** | A step dispatched by a worker running the new manifest uses the new inputs. A step **already queued** carries the inputs it was dispatched with — a polling or `WaitForSignal` step parked between attempts, the values its first attempt resolved. An expression in a queued step's inputs whose text you changed is passed to the handler as the literal string, because an input is only evaluated while it still matches the manifest's text. |

If you need an in-flight run to keep the graph it started with, see
[Changes that must not reach in-flight runs](#changes-that-must-not-reach-in-flight-runs).

### During a rolling deploy

While replicas are being restarted one by one, old and new versions consume the same queue at the
same time. Two things follow, and both are deterministic:

- **Whichever replica takes a decision applies its own manifest.** A step that completes on a
  not-yet-restarted replica is continued under the old graph; one that completes on a restarted
  replica, under the new one. A run that is active through the deploy window can therefore take
  some decisions under each version.
- **A replica never decides a step it does not know.** Every host writes its manifest to the flow
  store at startup, so the store holds the manifest of the most recently started replica. When a
  replica is handed a step that is not in its own manifest and the stored manifest differs from
  its own, another version is live: the replica hands the step back to the runtime (redelivered
  after 5 s, EventId `3020`) without claiming it or writing a status, and the run stays open until
  a replica that knows the step runs it. Only a replica whose manifest matches the stored one
  records an unknown step `Skipped` as removed (EventId `3021`). Once every replica runs the same
  version — the deploy finished, or was rolled back — the store agrees with all of them and every
  handed-back step is decided on its next delivery.

  Before 2.0, a not-yet-restarted replica handed a newly added step recorded it `Skipped`
  immediately. Its dependents were then cascade-skipped and the run could end `Skipped` with the
  new step never having run, while the in-memory runtime (single process, no mixed window) ran
  the same step — the divergence the stress harness found.

---

## Safe Changes — Deploy at Any Time

These changes behave the same for in-flight runs as for new runs, with no surprises:

- **Appending a step** after the current last step of a path. In-flight runs that have not yet
  completed the upstream step run it too; runs that already completed the upstream step run it
  on the next decision taken for that run — so a run that is still active picks it up, a run that
  has already finished does not.
- **Adding a new trigger** (manual, cron, webhook). Existing triggers are unaffected.
- **Changing the handler implementation.** Handlers are registered at runtime via
  `AddStepHandler<T>("StepType")` — they are not part of the persisted manifest.
- **Changing literal `Inputs` values** of a step that is never queued for long (not a polling or
  `WaitForSignal` step).
- **Bumping the `Version` string.**

---

## Caution Changes — Decide Whether In-Flight Runs Should See Them

Each of these is applied to in-flight runs by the rule above. Deploy freely if that outcome is what
you want; otherwise drain first, or ship a new flow.

| Change | What in-flight runs do | When to drain |
|---|---|---|
| Inserting a step in the middle of a path | A run that already passed the insertion point runs the new step immediately, out of its intended order (after steps that were meant to follow it). | When the new step must run before its successors — a validator, a guard, a reservation. |
| Adding an entry step, or a step after an early step most runs have passed | Runs once in every in-flight run. | When the step has side effects that must not happen twice or late (sending a message, charging). |
| Changing `RunAfter` of an existing step | Steps not yet scheduled are judged by the new edges; scheduled ones are not re-judged. | When a step must not start before a prerequisite you are adding. |
| Adding or changing a `When` clause | Applies only to steps scheduled after the deploy, so runs triggered moments apart can branch differently. | When every run must branch on the same condition. |
| Changing a step's `Type` | A step that has not started runs the new handler with inputs persisted under the old one. | When the new handler cannot read the old inputs. |
| Changing an expression in `Inputs` of a polling or `WaitForSignal` step | The parked step keeps the inputs it was dispatched with; a changed expression arrives as a literal string. | Always, for that step. |
| Removing or renaming a step that runs may be parked on | The parked or queued step is recorded `Skipped`; its dependents follow the new graph. | When the step's work must complete for runs already in it. |

The drain procedure (mirrors the *Production Checklist*):

1. In the dashboard, pause every cron trigger for the flow.
2. Wait for `GET /flows/api/runs/active` to contain no runs for that flow (the endpoint takes no
   `flowId` filter — filter the array client-side).
3. Deploy the change.
4. Resume the cron triggers.

### Changes that must not reach in-flight runs

FlowOrchestrator does not pin a run to the manifest it started under. When a change must not reach
runs that are already in flight and draining is not possible (for example a webhook-driven flow
whose callers cannot be paused), ship it as a **new flow with a new `Id`**: route new triggers to
it and leave the old flow registered until its runs have drained. Each flow then has exactly one
graph, and the old one keeps running the runs it started.

---

## Breaking Changes — Migrate Explicitly

These changes break run history readability or violate the `Id`-immutability rule. Do not do them
silently — the recipes below preserve the audit trail.

### Removing a step that has historical runs

Do **not** delete the step from the manifest. Run history still references the step by key in
`FlowSteps` rows, and dropping it makes the dashboard's run-detail view print "unknown step" for
every past run. (In-flight runs are fine either way: see the table above.)

**Recipe:** keep the step in the manifest but point its `Type` at a no-op handler, or guard it with
a false `When` clause on its `RunAfter` edge —
`RunAfter = new RunAfterCollection { ["fetch_orders"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "false" } }`
— so the planner marks it `Skipped` for new runs and for in-flight runs that have not reached it,
while history continues to render correctly.

### Renaming a step key

A step key (`"submit_to_wms"`) is part of the persisted run record. Renaming it makes every
historical row unreadable.

**Recipe — alias and skip:**

1. Add the new key (`"submit_to_wms_v2"`) alongside the old one with the desired downstream wiring.
2. Guard the old key with a false `When` clause on its `RunAfter` edge —
   `RunAfter = new RunAfterCollection { ["upstream"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "false" } }`
   — so new runs, and in-flight runs that reach it after the deploy, mark it `Skipped` and take the
   new key. (`When` lives on the `RunAfter` edge, not on `StepMetadata`; an entry step with no
   `RunAfter` cannot be guarded this way — point its `Type` at a no-op handler instead.)
3. Leave the old key in the manifest until the retention window has expired (default 30 days; see
   [Observability](observability.md#data-retention)) — by then no live run history references it
   and you can finally delete it.

### Changing the flow `Id`

Don't. The `Id` is the join key for `FlowDefinitions`, `FlowRuns`, `FlowRunControls`,
`FlowIdempotencyKeys` and `FlowScheduleStates` (the recurring-job state); `FlowSteps`,
`FlowOutputs`, `FlowStepDispatches` and `FlowStepClaims` hang off those rows by `RunId`. Changing
the `Id` orphans the whole tree and creates a brand-new flow with no history.

If you genuinely need a clean break (e.g. the original `Id` was generated by `Guid.NewGuid()` and
is now unstable across environments, or a change must not reach in-flight runs) the only safe move is:

1. Define a new flow class with a new fixed `Id`.
2. Leave the old flow registered and disable it — set `IsEnabled = false` on its flow record via
   the dashboard's Disable action or `POST /flows/api/flows/{id}/disable` — so the dashboard can
   still surface its history.
3. Cut over triggers (cron, webhook routes) to the new flow.

---

## Pre-Deploy Checklist

Before merging a flow change to production, walk through:

- [ ] Run the full test suite using [`FlowTestHost`](testing.md). New step or `When`? Add a test for it.
- [ ] For every added, inserted, removed or rewired step, read the
      [table above](#what-each-kind-of-change-does-to-a-run-that-is-in-flight) and decide whether
      in-flight runs should see it. Open the dashboard and check the **Active Runs** count for the
      flow — drain, or ship a new flow, if they must not.
- [ ] If the change touches storage (new step → new rows in `FlowOutputs`; new trigger → new
      recurring-job state), run the migration in a non-production environment first. The
      auto-migrator (`FlowOrchestratorSqlMigrator`) is idempotent, but verify before trusting it.
- [ ] If the cron schedule changed, **pause and resume** the trigger from the dashboard rather than
      relying on the auto-update path — Hangfire's `RecurringJob` honours the new expression on the
      next sync.
- [ ] Tag the release with a semantic version and bump the flow's `Version` string so the timeline
      view tells the story.

---

## Worked Example — `OrderFulfillmentFlow` Evolution

The sample at [`samples/FlowOrchestrator.SampleApp/Flows/OrderFulfillmentFlow.cs`](https://github.com/hoangsnowy/FlowOrchestrator/blob/main/samples/FlowOrchestrator.SampleApp/Flows/OrderFulfillmentFlow.cs) is a useful walking example because it has the three patterns most flows have: a query, a polling external call, and a save.

### v1.0 — Initial shape

```csharp
Steps =
{
    ["fetch_orders"]  = new StepMetadata { Type = "QueryDatabase",   /* … */ },
    ["submit_to_wms"] = new StepMetadata { Type = "CallExternalApi",
        RunAfter = { ["fetch_orders"] = [StepStatus.Succeeded] }, /* … */ },
    ["save_result"]   = new StepMetadata { Type = "SaveResult",
        RunAfter = { ["submit_to_wms"] = [StepStatus.Succeeded] }, /* … */ },
}
```

### v1.1 — Safe: append a notification step

We want to ping a Slack channel after every successful run. The new step has no
dependents, so it is a *safe* change — deploy any time. Runs still in flight when v1.1 lands
notify the warehouse too, once their `save_result` succeeds.

```csharp
Steps =
{
    ["fetch_orders"]  = /* unchanged */,
    ["submit_to_wms"] = /* unchanged */,
    ["save_result"]   = /* unchanged */,

    // New — appended after save_result, no existing step depends on it
    ["notify_warehouse"] = new StepMetadata { Type = "NotifySlack",
        RunAfter = { ["save_result"] = [StepStatus.Succeeded] },
        Inputs = { ["channel"] = "#warehouse" } },
}
```

Bump `Version` to `"1.1"`. Done.

### v2.0 — Caution: insert a validator in the middle

We've decided every order must be validated before submission. The new step
`validate_order` sits *between* `fetch_orders` and `submit_to_wms`.

```csharp
Steps =
{
    ["fetch_orders"]  = /* unchanged */,

    // NEW step inserted into the existing path
    ["validate_order"] = new StepMetadata { Type = "ValidateOrder",
        RunAfter = { ["fetch_orders"] = [StepStatus.Succeeded] } },

    // CHANGED — submit_to_wms now waits on validate_order, not fetch_orders
    ["submit_to_wms"] = new StepMetadata { Type = "CallExternalApi",
        RunAfter = { ["validate_order"] = [StepStatus.Succeeded] }, /* … */ },

    ["save_result"]   = /* unchanged */,
    ["notify_warehouse"] = /* unchanged */,
}
```

This is a *caution* change — an inserted step and a modified `RunAfter`. An in-flight run that
already passed `fetch_orders` — for example one whose `submit_to_wms` is still polling the WMS —
runs `validate_order` on its next decision, **after** the order was already submitted. Validating
after the fact is not what v2.0 means, so drain:

1. Pause cron + webhook for `OrderFulfillmentFlow`.
2. Wait for `GET /flows/api/runs/active` to contain no runs for this flow — the endpoint takes no `flowId` filter, so filter the returned array client-side (or use `GET /flows/api/runs?flowId=…` and check for `Running` entries).
3. Deploy.
4. Bump `Version` to `"2.0"`.
5. Resume the triggers.

If you cannot drain (e.g. the flow is webhook-driven and webhooks cannot be paused),
ship v2.0 as a new flow with a new `Id` and route new webhooks there — leaving v1.1 to drain
naturally under its own graph.
