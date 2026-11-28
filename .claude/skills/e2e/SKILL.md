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
| `d1f8a000-…-0201…0209` | `ProviderWebhookFlows.cs` (GitHub, Stripe, Slack, Twilio, Teams, Standard Webhooks, shared secret, rate-limited, IP allow-list) | Webhook provider matrix (5.6W). |

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

> **Authentication — every request below needs it.** The sample app enables dashboard
> Basic Auth (`samples/FlowOrchestrator.SampleApp/appsettings.json` → `FlowDashboard:BasicAuth`,
> `admin` / `admin`). Every `/flows/**` call therefore needs `-u admin:admin`; without it
> curl gets a bare `401` and every check fails for a reason that has nothing to do with the
> code under test. Webhook receive endpoints are the exception in both directions: Basic Auth
> does **not** open them; they authenticate the way the provider does (signature header for a
> signature scheme, `X-Webhook-Key` / `Authorization: Bearer` only for plain shared-secret
> triggers). See 5.6 and 5.6W.

#### 5.1 Dashboard surface

```bash
curl -fsS -u admin:admin "http://localhost:$port/flows/" -o /dev/null            # 200 — SPA
curl -fsS -u admin:admin "http://localhost:$port/flows/api/flows" | jq 'length'  # >= 12
```

`flow-sqlserver` reports 13 flows; the other three report 12 (`OrderFulfillmentFlow` is
SQL-only). Assert `>= 12`, not equality.

#### 5.2 Cron auto-trigger (HelloWorldFlow)

`HelloWorldFlow` runs `*/1 * * * *`, so a run appears within ~60 s.

The run-list projection has **no `triggerType` field** — it exposes `triggerKey`, the manifest
trigger name. `HelloWorldFlow`'s cron trigger is keyed `scheduled`, so filter on that. (A
`select(.triggerType == "Cron")` filter silently matches nothing and reports a healthy cron
as a failure.)

```bash
for i in $(seq 1 90); do
  count=$(curl -fsS -u admin:admin "http://localhost:$port/flows/api/runs?flowId=00000000-0000-0000-0000-000000000001" \
           | jq '[.[] | select(.triggerKey == "scheduled")] | length')
  [ "${count:-0}" -ge 1 ] && break
  sleep 1
done
[ "${count:-0}" -ge 1 ] || { echo "  cron never fired"; exit 1; }
```

#### 5.3 Manual trigger + completion (ParallelHealthCheckFlow)

```bash
runId=$(curl -fsS -u admin:admin -X POST "http://localhost:$port/flows/api/flows/00000000-0000-0000-0000-000000000006/trigger" \
        -H 'Content-Type: application/json' -d '{}' | jq -r '.runId')

for i in $(seq 1 30); do
  status=$(curl -fsS -u admin:admin "http://localhost:$port/flows/api/runs/$runId" | jq -r '.status')
  [ "$status" = "Succeeded" ] && break
  [ "$status" = "Failed"   ] && { echo "  run failed"; exit 1; }
  sleep 1
done
```

Step records come back on the run-detail payload as `.steps[]`; `/flows/api/runs/{runId}/steps`
returns the same collection standalone. Either is fine.

#### 5.4 ForEach iteration (OrderBatchFlow)

Trigger with a 3-item array, verify run completes Succeeded and reports 3 child completions.

```bash
runId=$(curl -fsS -u admin:admin -X POST "http://localhost:$port/flows/api/flows/00000000-0000-0000-0000-000000000005/trigger" \
        -H 'Content-Type: application/json' \
        -d '{"orderIds":[1,2,3],"items":[{"id":1},{"id":2},{"id":3}]}' | jq -r '.runId')
# Poll-to-Succeeded as in 5.3, then assert step count >= 4 (forEach + 3 children).
# A healthy run reports 9 steps: prepare_batch + process_orders + 3×(validate_order +
# archive_order) + finalize_batch. archive_order reads its sibling validate_order's output
# via "@steps('validate_order').output.note" (scope-relative resolution, issue #166).
```

#### 5.5 Skip semantics (ConditionalSkipDemoFlow + AmountThresholdFlow)

Two different skip reasons — the engine distinguishes them (`flow_step_skipped` carries
`reason` = `when_false` or `prerequisites_unmet`) and so must this check.

**`ConditionalSkipDemoFlow` (…0008) — `prerequisites_unmet`.** No payload. `validate_payment`
always fails, so `charge_customer` is skipped for want of a `Succeeded` predecessor. It carries
no `When` clause, so its `evaluationTraceJson` is **`null` — and that is the correct result.**
Assert: run `Succeeded`, skipped step is exactly `charge_customer`, trace is null.

**`AmountThresholdFlow` (…0012) — `when_false`.** Send `amount=10` and `amount=10000`. Both runs
succeed and both skip exactly **one** step, because the two branches are mutually exclusive —
so a count comparison proves nothing. What must differ is *which* step is skipped:

| Payload | Skipped step | Trace |
|---|---|---|
| `{"amount":10}` | `high_value_approve` | `{"expression":"@triggerBody().amount > 1000","resolved":"10 > 1000","result":false}` |
| `{"amount":10000}` | `auto_approve` | present |

Assert the two skipped step keys differ and that `evaluationTraceJson` is non-null on both.

#### 5.6 Webhook + HMAC (WebhookEnterpriseSampleFlow)

`WebhookEnterpriseSampleFlow.cs` configures the **GitHub** partner scheme: the signature header is
`X-Hub-Signature-256` and the HMAC covers the **raw body only**. The route is
`/flows/api/webhook/{slug}` (singular), the slug is `github-sample-flow`, and the secret is
`mySharedSecret` (set as both `webhookSecret` and `webhookHmacKey`). A success is **200**.

Send **exactly what GitHub sends** — GitHub never sends `X-Webhook-Key` or `X-Webhook-Timestamp`,
so neither appears below. (Until 2.0-P-1 this check sent both, which hid two real bugs: the legacy
`webhookSecret` field forced an `X-Webhook-Key` check that no GitHub delivery can pass, and the
trigger's replay window demanded a timestamp header GitHub does not have. Against `548bb7f` this
check therefore **fails with 401**; it passes once those fixes are in.)

```bash
body="{\"hello\":\"world\",\"n\":$RANDOM}"
SECRET='mySharedSecret'
sig=$(printf '%s' "$body" | openssl dgst -sha256 -hmac "$SECRET" | sed 's/^.*= //')
code=$(curl -s -o /dev/null -w "%{http_code}" -X POST        "http://localhost:$port/flows/api/webhook/github-sample-flow"        -H 'Content-Type: application/json' -H 'Accept:'        -H 'User-Agent: GitHub-Hookshot/044aadd'        -H 'X-GitHub-Event: ping'        -H "X-GitHub-Delivery: $(uuidgen)"        -H "X-Hub-Signature-256: sha256=$sig"        --data-binary "$body")
[ "$code" = "200" ] || { echo "  webhook rejected $code"; exit 1; }
```

The AppHost sets `Webhooks__EnforcementMode=Enforce` on every instance (the sample's standalone
default stays `Audit`), so rejections are real 4xx answers. Replay, signature-mismatch, body cap,
rate limit, IP allow-list and idempotency are covered by the webhook matrix below.

#### 5.6W Webhook provider matrix (`e2e-webhooks.sh`)

Run `bash .claude/skills/e2e/e2e-webhooks.sh [port …]` against the running AppHost (default: all four
ports). It needs `curl`, `jq` and `python`, signs every request with the provider's documented
algorithm (independent of the library), and sends each provider's own header set and `Content-Type` —
nothing extra. The flows it drives live in `samples/FlowOrchestrator.SampleApp/Flows/ProviderWebhookFlows.cs`
(ids `d1f8a000-…-0000000002NN`, also listed in the AppHost's `SampleFlowIds` for the Service Bus
subscriptions).

| Check | Flow / slug | Sent | Expect |
|---|---|---|---|
| W.1 | `e2e-github` | GitHub ping: `X-GitHub-Event`, `X-GitHub-Delivery`, `X-Hub-Signature-256`, hook-id headers | 200, run Succeeded, `X-GitHub-Event` in run `triggerHeaders` |
| W.1b | `github-sample-flow` | same, for the shipped sample flow (the corrected 5.6) | 200 |
| W.2 | `e2e-github` | the W.1 delivery again, byte-identical | 409 (accepted once) |
| W.3 | `e2e-github` | signature made with the wrong secret | 401 **and** a rejected (`isAccepted=false`, 401) row in `/flows/api/webhooks/recent?rejectedOnly=true` |
| W.4 | `e2e-stripe` | `Stripe-Signature: t=<now>,v1=…`, then the same delivery again | 200, then 409 |
| W.5 | `e2e-slack` | slash command, `application/x-www-form-urlencoded`, `X-Slack-Request-Timestamp` + `X-Slack-Signature` | 200 |
| W.6 | `e2e-twilio` | form POST, `X-Twilio-Signature` = base64 HMAC-SHA1 over URL + sorted fields, `I-Twilio-Idempotency-Token` | 200 |
| W.7 | `e2e-teams` | `Authorization: HMAC <base64>` (key = base64-decoded security token) | 200, no `Authorization` in run `triggerHeaders` |
| W.8 | `e2e-standard-webhooks` | `webhook-id` / `webhook-timestamp` / `webhook-signature: v1,…` (built-in `StandardWebhooks` scheme, key `whsec_…`); then a forged signature | 200, then 401 |
| W.9 | `e2e-shared-secret` | `X-Webhook-Key` right / wrong (this IS the shared-secret protocol) | 200 / 401 |
| W.10 | `e2e-shared-secret` | two deliveries with the same `Idempotency-Key` | same `runId`, second `duplicate=true`, exactly one new run |
| W.11 | `e2e-shared-secret` | 1.2 MB body with `Content-Length`, then chunked | 413 both |
| W.12 | `e2e-rate-limited` | 6 back-to-back deliveries (trigger: 1/s, burst 2) | first 200, a 429 among them |
| W.13 | `e2e-ip-allowlist` | allow-list `198.51.100.0/24`; direct call, then `X-Forwarded-For: 198.51.100.7, 203.0.113.9` | 403 both — with one trusted proxy hop the client is the **rightmost** entry (what the proxy appended); the forged prefix must not open the list |
| W.14 | `e2e-shared-secret` | raw socket: `Content-Length: 100000`, 1 000 bytes, close | no new run, `/health` 200; check the AppHost log for "unhandled exception" on that request |
| W.15 | `e2e-github` run (5102 only) | Keycloak `viewer` / `viewer-demo-pass` reads run detail | `X-Hub-Signature-256` = `***redacted***` for the viewer, in clear for `admin` |

Expected failures on `548bb7f` (baseline, before the 2.0-P-1 webhook fixes): W.1b (legacy
`webhookSecret` forces `X-Webhook-Key`), W.2 / W.1 under host replay (`missing_timestamp` 409 on the first
delivery), W.5 / W.6 (form bodies rejected as invalid JSON), W.7 (Teams signature header stripped,
key not base64-decoded), W.14 (unhandled `BadHttpRequestException`), W.8 (no `StandardWebhooks`
scheme — an unknown scheme name silently disabled verification, so the forged delivery got 200; the
built-in `StandardWebhooks` scheme now verifies it: genuine → 200, forged → 401). Still expected to fail
after those fixes, until fixed: **W.13** (X-Forwarded-For trusts the entry left of the one the proxy
appended).

Report the matrix as a separate block per instance and add its `RESULT: N/N` line to the summary.

#### 5.7 WaitForSignal resume (ApprovalWorkflowFlow)

```bash
runId=$(curl -fsS -u admin:admin -X POST "http://localhost:$port/flows/api/flows/00000000-0000-0000-0000-000000000007/trigger" \
        -H 'Content-Type: application/json' -d '{}' | jq -r '.runId')

# Wait for run to enter "waiting on signal" state.
for i in $(seq 1 30); do
  status=$(curl -fsS -u admin:admin "http://localhost:$port/flows/api/runs/$runId" | jq -r '.status')
  [ "$status" = "Running" ] && break
  sleep 1
done

# Send signal and watch run complete. The signal name comes from the manifest's
# WaitForSignal step input — "approval" for this flow.
curl -fsS -u admin:admin -X POST "http://localhost:$port/flows/api/runs/$runId/signals/approval" \
     -H 'Content-Type: application/json' -d '{"approved":true}'
```

Then poll-to-Succeeded as in 5.3.

#### 5.8 Hangfire dashboard (Hangfire instances only)

```bash
curl -fsS -o /dev/null "http://localhost:$port/hangfire"   # 200
```

For InMemory + ServiceBus instances, expect 404 — confirm absence so a regression that accidentally registers Hangfire is caught.

#### 5.9 SQL-only — OrderFulfillmentFlow (port 5101 only)

Trigger with `{"orderId":"E2E-1"}` and poll to terminal. Expect Succeeded within ~60 s across
`fetch_orders → submit_to_wms → save_result`.

Do **not** assert a `Pending` transition here. `submit_to_wms` uses `CallExternalApiStep`, which
derives from `PollableStepHandler<T>` but whose `FetchAsync` returns the API response on the
first attempt, and the manifest sets no minimum-attempt floor — so the step satisfies
immediately and legitimately never parks. Polling is covered by 5.10.

#### 5.10 Polling Pending → Succeeded (ShipmentTrackingFlow, all instances)

`ShipmentTrackingFlow` (…0003) is the flow that actually exercises the poll-and-reschedule
cycle, and it does so on every runtime — so this check is not SQL-only.

```bash
runId=$(curl -fsS -u admin:admin -X POST "http://localhost:$port/flows/api/flows/00000000-0000-0000-0000-000000000003/trigger" \
        -H 'Content-Type: application/json' -d '{"trackingNumber":"E2E-1"}' | jq -r '.runId')
# Poll ~1 s apart. You must OBSERVE check_shipment_status in Pending at least once
# before the run reaches Succeeded — sampling only the terminal state proves nothing.
```

Also assert the step's `attemptCount` on the run detail is **at least 3** — the flow declares
`pollMinAttempts = 3`. Seeing `Pending` once and a terminal `Succeeded` is not enough: on 1.34.0–1.34.7 a
Hangfire re-check ran with its inputs destroyed by Newtonsoft.Json, so `pollEnabled` read `false` and the
step succeeded on attempt **2** — and the old two-condition check passed regardless.

Healthy trace: `check_shipment_status` goes `Running → Pending → Pending → Succeeded`, then
`log_shipment_confirmed` runs and the run completes `Succeeded` (~15–20 s).

#### 5.11 Servers view (all instances)

Every process registers a heartbeat row in `IFlowServerStore` on start (SQL Server, PostgreSQL and in-memory
storage all implement it) and refreshes it every 15 s; `/flows/api/servers` lists the rows with a computed
`Active` / `Stale` status.

```bash
curl -fsS -u admin:admin "http://localhost:$port/flows/api/servers" \
  | jq '{tracked, active: [.servers[] | select(.status == "Active")] | length, runtimes: [.servers[].runtime] | unique}'
# tracked == true and active >= 1
```

Assert `tracked == true` and **at least one** `Active` server. Do not assert an exact count: on the two
database instances a previous process that was killed rather than stopped leaves its row behind, shown `Stale` until it is
purged an hour later, and a scaled-out deployment lists one row per replica. The runtime of the Active row is
`Hangfire` on 5101 / 5102, `InMemory` on 5103 and `ServiceBus` on 5104 — worth eyeballing, not asserted.
`tracked == false` means the instance's storage registered no `IFlowServerStore`: a wiring regression.

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
  ✓ 5.5 skip semantics
  ✓ 5.6 webhook + HMAC
  ✓ 5.7 WaitForSignal resume
  ✓ 5.8 Hangfire dashboard
  ✓ 5.9 SQL-only OrderFulfillmentFlow
  ✓ 5.10 polling Pending → Succeeded
  ✓ 5.11 servers view

=== flow-postgresql (5102) storage=postgresql runtime=hangfire ===
  ✓ 5.1 …
  …
  ⊘ 5.9 (SQL-only)
  ✓ 5.10 polling Pending → Succeeded

=== flow-inmemory (5103)   storage=inmemory   runtime=inmemory ===
  ✓ 5.1 …
  ⊘ 5.8 (no Hangfire — asserted 404)
  ⊘ 5.9 (SQL-only)
  ✓ 5.10 polling Pending → Succeeded

=== flow-servicebus (5104) storage=inmemory   runtime=servicebus ===
  …
```

End with a one-line verdict: `RESULT: 44/44 passed` or `RESULT: 42/44 passed — 2 failures listed above`.
The denominator is 11 checks × 4 instances; ⊘ rows count toward it (3 of them: `5.9` on the
three non-SQL instances), so a fully healthy run executes 41 checks and skips 3.

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
- **Webhook secrets**: read from `WebhookEnterpriseSampleFlow.cs` / `ProviderWebhookFlows.cs` rather than guessing. They are demo values committed on purpose so the e2e can sign like the real provider.

## When to invoke

- **After every PR** that touches `src/FlowOrchestrator.{Core,Hangfire,InMemory,ServiceBus,SqlServer,PostgreSQL,Dashboard}` or `samples/FlowOrchestrator.SampleApp`. Unit tests cover components in isolation; this skill proves they cooperate end-to-end.
- **Before tagging a release**. Adds confidence on top of the regression suite.
- **After a Docker / Aspire / .NET SDK upgrade**. Catches infrastructure-level breaks before they reach a release branch.

Do **NOT** invoke for documentation-only changes, dashboard CSS-only changes, or test-only PRs. The cost (~5 min) isn't worth the signal in those cases.
