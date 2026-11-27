# 1.34 → 2.0 upgrade rehearsal

Rehearses the upgrade in [`docs/articles/migrating-to-2.0.md`](../../../docs/articles/migrating-to-2.0.md)
end to end, the way a production customer performs it: a real app on the released 1.34.x packages,
a real database with live state, then the same app upgraded by following the guide, on the same
database. Run it before every 2.0 release and after any change to a migrator, the Hangfire job shape,
the Service Bus envelope, or the guide.

```powershell
pwsh tests/upgrade/FlowOrchestrator.UpgradeRehearsal/rehearse.ps1                 # all combinations; rollback on hangfire-sqlserver and servicebus-sqlserver
pwsh tests/upgrade/FlowOrchestrator.UpgradeRehearsal/rehearse.ps1 -Combination hangfire-postgresql -RollbackOn @()
pwsh tests/upgrade/FlowOrchestrator.UpgradeRehearsal/rehearse.ps1 -FromVersion 1.34.6   # another 1.34.x starting point
```

It prints a step → result table per combination, writes `report.md` into the work directory
(default `%TEMP%/fo-upgrade-rehearsal`), and ends with `RESULT: N/N passed` (exit code 1 otherwise).
About 8 minutes per combination.

**Needs** PowerShell 7.4+, Docker (one SQL Server *or* one PostgreSQL container at a time, plus the Service
Bus emulator for `servicebus-sqlserver`, which shares that SQL Server), the .NET 11 SDK, and the
**.NET 8 ASP.NET Core runtime** (the 1.34 app runs on `net8.0`, as most 1.34 hosts do).

## What is in here

| Path | What |
|---|---|
| `app/` | "OrderHub", the customer app, **as written for 1.34.8** (`net8.0`, `PackageReference`s to nuget.org). One source tree, several customers: `-p:CustomerRuntime=hangfire\|servicebus`, `-p:CustomerStorage=sqlserver\|postgresql` (the script pins them per work copy with a `Directory.Build.props`). |
| `upgrade-to-2.0.patch` | The diff a customer makes by following the guide: Step 1 (retarget to `net10.0`), Step 2 (package references: add `FlowOrchestrator.Core`, keep `FlowOrchestrator.Hangfire` only on Hangfire hosts), Step 3 (`using FlowOrchestrator.Hangfire` only where `UseHangfire()` is called) and Step 6 (delete `EnableEventPersistence`). Nothing else: if the guide misses a required change, the v2 build or a check fails. |
| `rehearse.ps1` | The driver. |
| `NuGet.config.template`, `servicebus/Config.template.json` | Feed and emulator configuration written into the work directory. |
| `../Directory.*.props` | Isolation stoppers, so the app never inherits the repository's build policy or Central Package Management. |

The app's flows cover what production hosts use: a webhook order flow signed with the GitHub HMAC
scheme with delivery-id replay protection and `Enforce` mode, a `ForEach`, a `When` branch, a cron flow
whose schedule is overridden from the dashboard and one whose schedule is paused, a `WaitForSignal`
approval with a per-run `timeoutSeconds` and an escalation branch, a `PollableStepHandler` carrier poll
(in a plain flow and inside a `ForEach`), a payment flow deduplicated by `Idempotency-Key`, Basic Auth on
the dashboard, `PersistOverrides`, retention, and `EnableEventPersistence` (so the 1.34 database has run
events).

`Rehearsal:Workers=false` starts a web-only node (no Hangfire server, no Service Bus processors) so work
can be left queued but unexecuted across the upgrade; `Rehearsal:ShutdownEndpoint=true` maps
`POST /rehearsal/shutdown` for a graceful stop on every OS. Neither is part of the upgrade.

## What a run does, per combination

1. Packs this checkout as `2.0.0-rehearsal.1` into a local feed, builds `app/` on 1.34.x and the patched
   copy on 2.0, and runs the guide's Step 2 `dotnet list package --include-transitive` check.
2. Starts a fresh database. On 1.34: two signed webhook deliveries (both `When` branches), a replay
   (`409`) and a forged signature (`401`); an idempotent payment and its duplicate; a declined (failed)
   payment; an approval parked in `WaitForSignal`; a cancelled approval; a carrier poll left `Pending`; a
   `ForEach` stopped with item 0 done and item 1 polling; a cron override and a paused schedule (and the
   override firing); an approval with a 150 s timeout. Then, from a web-only node, a webhook run, an
   approval and a payment whose jobs stay `Enqueued`.
3. Stops 1.34 and runs the guide's pre-flight: the Step 0 inventory search, the Step 4.1 query verbatim,
   a check that the queued 1.34.8 re-checks carry `__hangfireJsonInputs` (so the 2.0 side exercises its
   decode), and a schema / row-count / run-events fingerprint.
4. Starts 2.0 web-only (migrator only), diffs the schema and the data, then starts 2.0 with workers and
   checks: the queued 1.34 jobs run, the parked run resumes on a signal, the 1.34 timeout fires on 2.0,
   the poll keeps polling, its attempt counter carries on rising (from 1.34.8; Hangfire ≤ 1.34.7 restarts it),
   and it completes, the `ForEach` finishes, the override is
   kept and fires once per tick, the paused schedule stays paused, webhooks verify, a 1.34 replay nonce
   is still rejected, a 1.34 idempotency key still deduplicates, the dashboard lists / searches /
   filters the 1.34 history and derives its Events timeline, new runs work, and the Step 9 checklist
   (health, flows and enabled flags, no failed Hangfire jobs, no error logs, no 4103 / 4104).
5. On the `-RollbackOn` combinations, follows the guide's Rollback section back to 1.34 and checks its
   claims (a queued 2.0 job on a 1.x worker, runs parked on 2.0, the Events drawer for 2.0 runs,
   idempotency keys written by 2.0, cron).

Containers are named `fo-rehearsal-*`; the script removes them at the end (`-KeepContainers` leaves them
for inspection) and before it starts. Logs of every app start are in `<work dir>/<combination>/*.log`.
