# FlowOrchestrator Samples

`FlowOrchestrator.SampleApp` is one ASP.NET Core app that the Aspire AppHost
(`FlowOrchestrator.AppHost`) runs **four times**, pinned to different (storage, runtime)
combinations — so every sample flow below runs on every backend:

| Instance | URL | Storage | Runtime |
|---|---|---|---|
| `flow-sqlserver`  | http://localhost:5101 | SQL Server | Hangfire |
| `flow-postgresql` | http://localhost:5102 | PostgreSQL | Hangfire |
| `flow-inmemory`   | http://localhost:5103 | InMemory   | InMemory |
| `flow-servicebus` | http://localhost:5104 | InMemory   | Azure Service Bus (emulator) |

```bash
dotnet run --project ./FlowOrchestrator.AppHost/FlowOrchestrator.AppHost.csproj   # needs Docker
```

Dashboard: `/flows` on each instance, Basic Auth `admin` / `admin`.

## Core-feature coverage map

Which core capability each flow exists to demonstrate. When something breaks in the engine,
this table says which flow makes it visible on the dashboard.

| Flow (ID suffix) | Core surface exercised | Watch it here |
|---|---|---|
| `HelloWorldFlow` (…0001) | Cron trigger (`*/1 * * * *`), `IRecurringTriggerDispatcher`, schedule overrides (`Scheduler.PersistOverrides`) | A new run every minute; edit the cron in the dashboard and it survives restart |
| `OrderFulfillmentFlow` (…0002, SQL only) | Business-table access, `PollableStepHandler<T>` against a real API, `@steps()` chaining into a DB write | `fetch_orders → submit_to_wms → save_result` |
| `ShipmentTrackingFlow` (…0003) | Poll-and-reschedule cycle: `Pending` + `DelayNextStep`, dispatch release/re-claim | `check_shipment_status` goes `Running → Pending → … → Succeeded` |
| `PaymentEventFlow` (…0004) | Webhook trigger, `@triggerBody()` path expressions, `@triggerHeaders()` | POST to `/flows/api/webhook/payment-event` |
| `OrderBatchFlow` (…0005) | ForEach fan-out, per-iteration inputs (`__loopItem`/`__loopIndex`), scope-relative `@steps()` sibling reads (issue #166), idempotency keys (`Idempotency-Key` header) | 3-item batch → 9 steps; replay the same Idempotency-Key and get the same RunId |
| `ParallelHealthCheckFlow` (…0006) | Multiple DAG entry steps, all-of join, partial-failure tolerance, graph validation | Fan-out/fan-in on the run graph |
| `ApprovalWorkflowFlow` (…0007) | `WaitForSignal` park + resume, signal API | "Send Signal" button on the parked step |
| `ConditionalSkipDemoFlow` (…0008) | Skip on unmet prerequisites (`prerequisites_unmet`), failure-handler branch | `charge_customer` Skipped, `handle_decline` runs |
| `SkipVariantsDemoFlow` (…0009) | Mid-chain skip vs. dead-end skip in one run | Two skip shapes side by side |
| `DeadEndSkipDemoFlow` (…0010) | Entry failure → transitive skip → run-level `Failed` classification | `RunTerminationClassifier` rules 1–2 |
| `FinalStepSkipDemoFlow` (…0011) | Leaf skip with run-level `Succeeded` classification | Classifier rule 3–4 |
| `AmountThresholdFlow` (…0012) | `When` clauses on `RunAfter` (`when_false` skips + evaluation traces) | Trigger with `{"amount":10}` vs `{"amount":10000}` |
| `WarehouseRobotFlow` (…0013) | **Loop completion barrier (issue #169, v1.30.1)**: `WaitForSignal` inside ForEach, loop step `Running` until every iteration is terminal, downstream gated on the loop; scope-relative sibling reads; loop-output reads (`@steps('scan_process').output.iterations`) | See below |
| `WebhookEnterpriseSampleFlow` (…0125) | Webhook hardening (v1.25): HMAC (GitHub scheme), replay nonces, rate limit, DLQ | "Webhooks" dashboard tab |

Cross-cutting features every run exercises: run timeout latch (`RunControl.DefaultRunTimeout`),
retention sweep (`Retention`), event persistence + OpenTelemetry (`Observability` — spans, metrics,
and structured logs land in the Aspire dashboard), health checks (`/health`), crash recovery
(`FlowRunRecoveryHostedService` re-dispatches on startup).

## WarehouseRobotFlow — the "like production" sample

The exact manifest shape from issue #169, driven end-to-end by a **simulated robot**
(`RobotSimulatorHostedService`), so one trigger plays out a realistic multi-step job while you
watch:

```
scan_start ─► scan_process ─► [0] wait_robot_goto ─► open_camera      (robot drives ~2-4 s per stop)
   (log)      (ForEach,       [1] wait_robot_goto ─► open_camera
               Running the    [2] wait_robot_goto ─► open_camera
               whole time)                                        └──► robot_callback_success
```

Trigger it (any instance):

```bash
curl -u admin:admin -X POST "http://localhost:5101/flows/api/flows/00000000-0000-0000-0000-000000000013/trigger" \
  -H 'Content-Type: application/json' \
  -d '{"OrderNo":"WH-1042","Locations":["A-01-03","A-02-07","B-11-01"]}'
```

Then open the run in the dashboard and watch:

- `scan_process` stays **Running** while the robot works — before v1.30.1 it reported
  `Succeeded` at fan-out and `robot_callback_success` ran *first*, which is the bug the loop
  completion barrier fixed.
- each iteration parks on `wait_robot_goto` (Pending), resumes when the simulator delivers the
  signal, and `open_camera` logs the location **its own** waiter reported — a wrong-scope
  `@steps()` resolve would photograph the same location three times.
- `robot_callback_success` appears only after the last capture, reading the loop's own output
  for the summary (`ScannedCount`).

The app log tells the same story in order (`[RobotSimulator] robot driving…`,
`[WarehouseRobot] camera captured…`, `…scan job complete: 3 location(s) scanned`), and the
run's Events tab shows the engine's own record (`step.pending` for the parked loop,
`step.completed` for the loop only at the very end).

The simulator talks to the app exclusively through public surfaces (`IFlowRunStore`,
`IFlowSignalDispatcher`) — the same integration surface a real robot controller would use.
Set `ROBOT_SIMULATOR=false` to drive the signals yourself:

```bash
curl -u admin:admin -X POST "http://localhost:5101/flows/api/runs/<runId>/signals/robot_goto" \
  -H 'Content-Type: application/json' -d '{"Location":"A-01-03"}'
```

## Where the rest of the core is proven

Samples are demos, not the safety net. The engine's invariants live in the test tree:

- `tests/unit/` — planner, barrier (`LoopBarrierTests`), classifier, expression resolution.
- `tests/integration/FlowOrchestrator.Testing.IntegrationTests/` — full engine + InMemory
  runtime end-to-end, including the issue #166/#169 manifests verbatim
  (`ForEachSignalSiblingTests`, `ForEachLoopBarrierTests`, `ForEachLoopBarrierEdgeCaseTests`).
- `tests/integration/FlowOrchestrator.{SqlServer,PostgreSQL,ServiceBus,Hangfire,Dashboard}.IntegrationTests/`
  — per-backend contracts (Testcontainers / emulator).
- `tests/regression/` — timing-sensitive cron/polling/timeout and concurrency stress.
- `.claude/skills/e2e` — the four-instance smoke matrix run before every release.
