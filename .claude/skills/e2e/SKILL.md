End-to-end smoke test for the FlowOrchestrator sample app under .NET Aspire. Runs the full feature matrix against four sample-app instances pinned to different (storage, runtime) combinations, then tears the host down. Use after a PR lands on `src/FlowOrchestrator.{Core,Hangfire,InMemory,ServiceBus,SqlServer,PostgreSQL,Dashboard}` or `samples/FlowOrchestrator.SampleApp`. The skill is invoked by the main thread or by `qa-agent` once unit tests pass.

## Stack under test

`FlowOrchestrator.AppHost/Program.cs` declares four instances, each pinned to a fixed HTTP port:

| Instance | URL | Storage | Runtime | Notes |
|---|---|---|---|---|
| `flow-sqlserver`  | http://localhost:5101 | SQL Server | Hangfire   | Full feature matrix incl. `OrderFulfillmentFlow`. |
| `flow-postgresql` | http://localhost:5102 | PostgreSQL | Hangfire   | All flows except `OrderFulfillmentFlow`. |
| `flow-inmemory`   | http://localhost:5103 | InMemory   | InMemory   | No `/hangfire` endpoint. No external DB. |
| `flow-servicebus` | http://localhost:5104 | InMemory   | ServiceBus | Topology pre-declared by Aspire emulator. |

Aspire bootstraps Docker resources automatically (`AddSqlServer`, `AddPostgres`, `AddAzureServiceBus().RunAsEmulator()`) — Docker Desktop must be running.

## Sample flows the skill exercises

Stable IDs from `FlowOrchestrator.AppHost/Program.cs:SampleFlowIds`.

| Flow ID | Class | Feature surface |
|---|---|---|
| `…0001` | `HelloWorldFlow`              | Cron — fires automatically. |
| `…0002` | `OrderFulfillmentFlow`        | SQL-only; manual trigger; polling step. |
| `…0003` | `ShipmentTrackingFlow`        | `PollableStepHandler<T>`. |
| `…0004` | `PaymentEventFlow`            | Webhook. |
| `…0005` | `OrderBatchFlow`              | `ForEach` step. |
| `…0006` | `ParallelHealthCheckFlow`     | Fan-out via `runAfter`. |
| `…0007` | `ApprovalWorkflowFlow`        | `WaitForSignal`. |
| `…0008` | `ConditionalSkipDemoFlow`     | `When` clause skip propagation. |
| `…0009` | `SkipVariantsDemoFlow`        | All Skipped → run terminal Skipped. |
| `…0010` | `DeadEndSkipDemoFlow`         | Mid-DAG skip with no recovery. |
| `…0011` | `FinalStepSkipDemoFlow`       | Leaf skip + run completes Skipped. |
| `…0012` | `AmountThresholdFlow`         | `When` against `@triggerBody().amount`. |
| `…0125` | `WebhookEnterpriseSampleFlow` | v1.25 webhook hardening (HMAC + replay). |

## Run protocol

Run phases in order. Stop on any failure and report what failed plus which earlier phases passed.

### Phase 1 — Preflight

```bash
docker info >/dev/null 2>&1 || { echo "Docker daemon not reachable"; exit 1; }
dotnet --version
```

If Docker is down, abort and ask the user to start Docker Desktop. Do not try to start it yourself.

### Phase 2 — Build

```bash
dotnet build FlowOrchestrator.slnx --configuration Debug 2>&1 | tail -15
```

Must show `0 Warning(s)` `0 Error(s)`. If not, abort and report the error verbatim.

### Phase 3 — Start the AppHost (background)

```bash
dotnet run --project ./FlowOrchestrator.AppHost/FlowOrchestrator.AppHost.csproj --configuration Debug
```

Run with `run_in_background: true`. Capture the task ID. The AppHost stays up for the rest of the run.

First start spins up SQL Server + PostgreSQL + Service Bus emulator containers — expect 60–180 s before all four instances are ready. Subsequent runs reuse data volumes and start in 30–60 s. First-time image pull on a fresh machine takes ~5 min.

### Phase 4 — Wait for readiness

Poll each `/health` endpoint until 200 or budget exhaust. Generous wall-clock — Aspire orchestration is not fast.

```bash
for port in 5101 5102 5103 5104; do
  echo "Waiting on http://localhost:$port/health …"
  for i in $(seq 1 120); do
    code=$(curl -s -o /dev/null -w "%{http_code}" "http://localhost:$port/health" || echo 000)
    [ "$code" = "200" ] && { echo "  ready ($code)"; break; }
    sleep 2
  done
  if [ "$code" != "200" ]; then
    echo "  TIMEOUT — last status $code"; exit 1
  fi
done
```

If one instance times out but others ready, that's diagnostic — read AppHost background output via the task ID and attach the relevant lines.

### Phase 5 — Feature matrix per instance

Per ready instance, run every check below. Skip checks marked **(SQL only)** for non-SqlServer instances. Skip the `/hangfire` check for InMemory + ServiceBus instances. Aggregate per-check results into a table.

#### 5.1 Dashboard surface

```bash
curl -fsS "http://localhost:$port/flows/" -o /dev/null            # 200 — SPA
curl -fsS "http://localhost:$port/flows/api/flows" | jq 'length'  # >= 12
```

#### 5.2 Cron auto-trigger (HelloWorldFlow)

Schedule fires within ~60 s. Poll the runs feed for at least one `Cron`-triggered run keyed to flow `…0001`:

```bash
for i in $(seq 1 60); do
  count=$(curl -fsS "http://localhost:$port/flows/api/runs?flowId=00000000-0000-0000-0000-000000000001" \
           | jq '[.[] | select(.triggerType == "Cron")] | length')
  [ "${count:-0}" -ge 1 ] && break
  sleep 1
done
[ "${count:-0}" -ge 1 ] || { echo "  cron never fired"; exit 1; }
```

#### 5.3 Manual trigger + completion (ParallelHealthCheckFlow)

```bash
runId=$(curl -fsS -X POST "http://localhost:$port/flows/api/flows/00000000-0000-0000-0000-000000000006/trigger" \
        -H 'Content-Type: application/json' -d '{}' | jq -r '.runId')

for i in $(seq 1 30); do
  status=$(curl -fsS "http://localhost:$port/flows/api/runs/$runId" | jq -r '.status')
  [ "$status" = "Succeeded" ] && break
  [ "$status" = "Failed"   ] && { echo "  run failed"; exit 1; }
  sleep 1
done
```

#### 5.4 ForEach iteration (OrderBatchFlow)

Trigger with a 3-item array, verify run completes Succeeded and reports 3 child completions.

```bash
runId=$(curl -fsS -X POST "http://localhost:$port/flows/api/flows/00000000-0000-0000-0000-000000000005/trigger" \
        -H 'Content-Type: application/json' \
        -d '{"items":[{"id":1},{"id":2},{"id":3}]}' | jq -r '.runId')
# Poll-to-Succeeded as in 5.3, then assert step count >= 4 (forEach + 3 children).
```

#### 5.5 When clause skip (ConditionalSkipDemoFlow + AmountThresholdFlow)

For `ConditionalSkipDemoFlow`, no payload — run completes Succeeded with at least one Skipped step. For `AmountThresholdFlow`, send two runs: `amount=10` (skip path) and `amount=10000` (full path), assert run statuses differ. Read the run detail's `evaluationTraceJson` field on the skipped step — must be non-null.

#### 5.6 Webhook + HMAC (WebhookEnterpriseSampleFlow)

Pull the webhook secret from `WebhookEnterpriseSampleFlow.cs`. Compute SHA256 HMAC of the body, post with `X-Signature` and `X-Timestamp`:

```bash
body='{"event":"payment.succeeded","amount":42}'
ts=$(date +%s)
sig=$(printf '%s.%s' "$ts" "$body" | openssl dgst -sha256 -hmac "$SECRET" -binary | xxd -p -c 256)
code=$(curl -s -o /dev/null -w "%{http_code}" -X POST \
       "http://localhost:$port/flows/api/webhooks/payments-enterprise" \
       -H 'Content-Type: application/json' \
       -H "X-Timestamp: $ts" \
       -H "X-Signature: sha256=$sig" \
       -d "$body")
[ "$code" = "202" ] || { echo "  webhook rejected $code"; exit 1; }
```

If the secret isn't exposed to the skill, mark **N/A** and note that webhook hardening must be smoke-tested manually.

#### 5.7 WaitForSignal resume (ApprovalWorkflowFlow)

```bash
runId=$(curl -fsS -X POST "http://localhost:$port/flows/api/flows/00000000-0000-0000-0000-000000000007/trigger" \
        -H 'Content-Type: application/json' -d '{}' | jq -r '.runId')

# Wait for run to enter "waiting on signal" state.
for i in $(seq 1 30); do
  status=$(curl -fsS "http://localhost:$port/flows/api/runs/$runId" | jq -r '.status')
  [ "$status" = "Running" ] && break
  sleep 1
done

# Send signal and watch run complete.
curl -fsS -X POST "http://localhost:$port/flows/api/runs/$runId/signals/approval" \
     -H 'Content-Type: application/json' -d '{"approved":true}'
```

Then poll-to-Succeeded as in 5.3.

#### 5.8 Hangfire dashboard (Hangfire instances only)

```bash
curl -fsS -o /dev/null "http://localhost:$port/hangfire"   # 200
```

For InMemory + ServiceBus instances, expect 404 — confirm absence so a regression that accidentally registers Hangfire is caught.

#### 5.9 SQL-only — OrderFulfillmentFlow (port 5101 only)

Trigger and watch through the polling loop. Expect Succeeded within ~60 s. Inspect `/flows/api/runs/{runId}/steps` for at least one Pending → Succeeded transition on the polling step.

### Phase 6 — Tear down

Stop the AppHost task by ID (TaskStop or equivalent). Wait up to 30 s for graceful exit, then force. Aspire shuts containers gracefully on SIGINT.

## Reporting format

One block per instance, one row per check. Use ✓ ✗ ⊘ (skipped) ⏱ (timeout) — emoji exception to CLAUDE.md, justified by at-a-glance matrix scanning.

```
=== flow-sqlserver (5101)  storage=sqlserver runtime=hangfire ===
  ✓ 5.1 dashboard
  ✓ 5.2 cron auto-trigger
  ✓ 5.3 manual trigger + completion
  ✓ 5.4 ForEach iteration
  ✓ 5.5 When clause skip
  ✓ 5.6 webhook + HMAC
  ✓ 5.7 WaitForSignal resume
  ✓ 5.8 Hangfire dashboard
  ✓ 5.9 SQL-only OrderFulfillmentFlow

=== flow-postgresql (5102) storage=postgresql runtime=hangfire ===
  ✓ 5.1 …
  …
  ⊘ 5.9 (SQL-only)

=== flow-inmemory (5103)   storage=inmemory   runtime=inmemory ===
  ✓ 5.1 …
  ⊘ 5.8 (no Hangfire)
  ⊘ 5.9 (SQL-only)

=== flow-servicebus (5104) storage=inmemory   runtime=servicebus ===
  …
```

End with a one-line verdict: `RESULT: 36/36 passed` or `RESULT: 34/36 passed — 2 failures listed above`.

If any instance failed Phase 4 readiness:
```
=== flow-postgresql (5102) ⏱ NEVER READY ===
  AppHost log tail:
    <last 20 lines of the relevant Aspire output>
```

## Operational notes

- **Aspire dashboard**: the AppHost binds an Aspire dashboard on a random port and prints its URL on startup. Not needed for the smoke run — per-instance `/health` and REST API are sufficient.
- **First-run cost**: pulling SQL Server, Postgres, and Service Bus emulator images for the first time on a fresh machine takes ~5 min. Tell the user before running so they don't kill it thinking it's stuck.
- **Container leftovers**: Aspire keeps containers warm. If a previous run wedged one, `docker ps -a --filter label=com.docker.compose.project=flow-orchestrator-apphost` shows the lineage; `docker rm -f <id>` clears it. Do NOT prune unrelated containers.
- **Port conflicts**: 5101–5104 and 7101–7104 are pinned. If any taken, abort and tell the user — do not silently re-route.
- **Webhook secrets**: read from `WebhookEnterpriseSampleFlow.cs` rather than guessing. Plain-secret default is documented in the file's XML header.

## When to invoke

- **After every PR** that touches `src/FlowOrchestrator.{Core,Hangfire,InMemory,ServiceBus,SqlServer,PostgreSQL,Dashboard}` or `samples/FlowOrchestrator.SampleApp`. Unit tests cover components in isolation; this skill proves they cooperate end-to-end.
- **Before tagging a release**. Adds confidence on top of the regression suite.
- **After a Docker / Aspire / .NET SDK upgrade**. Catches infrastructure-level breaks before they reach a release branch.

Do **NOT** invoke for documentation-only changes, dashboard CSS-only changes, or test-only PRs. The cost (~5 min) isn't worth the signal in those cases.
