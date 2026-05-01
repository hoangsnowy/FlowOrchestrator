# Versioning Flows in Production

Once a flow has run in production, its definition is no longer a free-form artifact —
every change must be reasoned about against in-flight runs, the run history, and the
recurring-job state stored by the runtime. This page is the rule-set for changing a
deployed flow safely.

## The Mental Model — Three Layers of Version

FlowOrchestrator separates *identity*, *display*, and *shape*:

| Layer | Type | Mutability | Effect of changing |
|---|---|---|---|
| **Flow `Id`** | `Guid` | **Immutable** — never change after first deploy. | All historical runs, recurring jobs, dispatch ledger entries, and webhook routes are keyed by `Id`. Changing it orphans every existing record and creates a new flow. |
| **Flow `Version` string** | `string` | Free-form display label. | Cosmetic only. Bump it whenever you ship a meaningful change so the dashboard and run history make the timeline legible. |
| **Manifest shape** | `FlowManifest` | Mutable, but each change has consequences. | The actual graph — triggers, steps, `RunAfter`, `When`, `Inputs`. The rest of this page is about which manifest changes are safe and which need a maintenance window. |

> [!IMPORTANT]
> The `Id` is stored in the database and used to route Hangfire jobs and webhook callbacks across all runtimes. **Never change it** — see [Getting Started](getting-started.md#define-your-first-flow).

---

## Safe Changes — Deploy at Any Time

These changes do not touch in-flight runs. No drain, no maintenance window.

- **Adding a brand-new step that no existing step depends on.** New runs pick it up; old runs do not retroactively gain it.
- **Adding a new trigger** (manual, cron, webhook). Existing triggers are unaffected.
- **Changing step `Inputs` values.** Inputs are resolved at step-execution time, so the change applies to any step that has not yet started. Topology is unchanged.
- **Changing the handler implementation.** Handlers are registered at runtime via `AddStepHandler<T>("StepType")` — they are not part of the persisted manifest.
- **Bumping the `Version` string.**

---

## Caution Changes — Drain First

These changes are safe only if no runs are in flight, because they can change the readiness rules for steps that have already started.

| Change | Failure mode if not drained | Recommendation |
|---|---|---|
| Inserting a step in the *middle* of an existing path | An in-flight run that already passed the upstream step will **not** retroactively run the new step. The run continues with the old graph it was planned against. | Pause cron, wait for `Active Runs == 0` on the dashboard, deploy. |
| Changing `RunAfter` dependencies on an existing step | A step that is already queued on the runtime may dispatch under the old plan and produce results inconsistent with the new edge set. | As above — drain. |
| Adding a new `When` condition to an existing step | A run that already passed the upstream gate will continue under the old condition, so two runs of "the same flow" can take divergent branches depending on when they were triggered. | Drain, or use a new step with the condition rather than modifying the existing one. |
| Changing a step's `Type` to point at a different handler | Re-runs of historical runs will use the new handler, which may not understand the old persisted inputs. | Drain. If you also need to re-run history, snapshot the manifest before the change so you can read back what each run was planned against. |

The drain procedure (mirrors the *Production Checklist*):

1. In the dashboard, pause every cron trigger for the flow.
2. Wait for `GET /flows/api/runs/active` to return `[]` for that flow.
3. Deploy the change.
4. Resume the cron triggers.

---

## Breaking Changes — Migrate Explicitly

These changes break run history readability or violate the `Id`-immutability rule. Do not do them silently — the recipes below preserve the audit trail.

### Removing a step that has historical runs

Do **not** delete the step from the manifest. Run history still references the step by key in `FlowSteps` rows, and dropping it makes the dashboard's run-detail view print "unknown step" for every past run.

**Recipe:** keep the step in the manifest but mark its `Type` as a no-op handler or set `Disabled = true` so the planner skips it for new runs while history continues to render correctly.

### Renaming a step key

A step key (`"submit_to_wms"`) is part of the persisted run record. Renaming it makes every historical row unreadable.

**Recipe — alias and skip:**

1. Add the new key (`"submit_to_wms_v2"`) alongside the old one with the desired downstream wiring.
2. Mark the old key as `Disabled` (or guard it with `When = "false"`) so new runs skip it.
3. Leave the old key in the manifest until the retention window has expired (default 30 days; see [Observability](observability.md#data-retention)) — by then no live run history references it and you can finally delete it.

### Changing the flow `Id`

Don't. The `Id` is the join key for every row in `FlowRuns`, `FlowSteps`, `FlowOutputs`, `FlowStepDispatches`, `FlowStepClaims`, `FlowRunControls`, `FlowIdempotencyKeys`, and the recurring-job state. Changing it creates a brand-new flow with no history.

If you genuinely need a clean break (e.g. the original `Id` was generated by `Guid.NewGuid()` and is now unstable across environments) the only safe move is:

1. Define a new flow class with a new fixed `Id`.
2. Leave the old flow registered with `Disabled = true` so the dashboard can still surface its history.
3. Cut over triggers (cron, webhook routes) to the new flow.

---

## Pre-Deploy Checklist

Before merging a flow change to production, walk through:

- [ ] Run the full test suite using [`FlowTestHost`](testing.md). New step or `When`? Add a test for it.
- [ ] Open the dashboard and confirm the **Active Runs** count for this flow is what you expect — drain if the change is in the *Caution* category above.
- [ ] If the change touches storage (new step → new rows in `FlowOutputs`; new trigger → new recurring-job state), run the migration in a non-production environment first. The auto-migrator (`FlowOrchestratorSqlMigrator`) is idempotent, but verify before trusting it.
- [ ] If the cron schedule changed, **pause and resume** the trigger from the dashboard rather than relying on the auto-update path — Hangfire's `RecurringJob` honours the new expression on the next sync.
- [ ] Tag the release with a semantic version and bump the flow's `Version` string so the timeline view tells the story.

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
dependents, so it is a *safe* change — deploy any time.

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

This is a *caution* change — both an inserted step and a modified `RunAfter`. An
in-flight run that already passed `fetch_orders` will continue under the v1.1
graph; it will *not* retroactively run `validate_order`. To avoid that ambiguity:

1. Pause cron + webhook for `OrderFulfillmentFlow`.
2. Wait for `GET /flows/api/runs/active?flowId=…` → `[]`.
3. Deploy.
4. Bump `Version` to `"2.0"`.
5. Resume the triggers.

If you cannot drain (e.g. the flow is webhook-driven and webhooks cannot be paused),
the safer alternative is to ship as a new flow with a new `Id` and route new
webhooks there — leaving v1.1 to drain naturally.
