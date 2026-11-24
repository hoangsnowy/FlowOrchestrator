# Switching Storage Backend (SQL Server ↔ PostgreSQL)

`flow-orchestrator storage copy` moves a FlowOrchestrator database from SQL Server to PostgreSQL, or from
PostgreSQL to SQL Server, **with its run history**: flow definitions, runs, steps, attempts, outputs, run
controls, idempotency keys, schedule overrides, signal waiters and the webhook logs. The source is only
read, so it stays a complete rollback point. The target is created with the target backend's own 2.0
migrator, so it is exactly the schema a 2.0 host creates.

The tool is the `FlowOrchestrator.Cli` .NET tool; the same engine is available as a library,
`FlowOrchestrator.StorageMigration` (`FlowStorageCopier`), for teams that script their own migration.

```bash
dotnet tool install --global FlowOrchestrator.Cli        # add --prerelease for a 2.0.0-alpha.N build
flow-orchestrator storage copy --help
```

Use the tool version that matches the FlowOrchestrator version of your hosts. A schema with a column the
tool does not know is refused rather than copied without it.

## Runbook

Work through every step. The commands below copy SQL Server → PostgreSQL; swap `--from` / `--to` (and the
connection strings) for the other direction.

### 0. Before the maintenance window

- **Both sides on 2.0.** The hosts that write the source must already run FlowOrchestrator 2.0 (its
  migrator brings the schema up to date at start-up). A 1.x schema is refused; see
  [Migrating to 2.0](migrating-to-2.0.md) first, or pass `--migrate-source` (runs the 2.0 migrator on the
  source: additive only — new tables, nullable columns, indexes).
- **An empty target database.** Create it (`CREATE DATABASE`); the tool creates the tables. Do **not** point
  a FlowOrchestrator host at it first — its start-up writes flow definitions, and the copy refuses a target
  that holds FlowOrchestrator rows.
- **Permissions.** Source: read. Target: create tables and indexes (the migrator's rights); on SQL Server
  `INSERT` and `ALTER` on the FlowOrchestrator tables (the bulk load keeps the copied identity values and
  re-seeds the identity afterwards); on PostgreSQL `INSERT` (`COPY ... FROM STDIN`) and, optionally,
  `CREATE EXTENSION pg_trgm` for the run-search indexes (without it search still works, as a scan, and the
  migrator logs a warning).
- **Keep passwords off the command line.** Every connection value accepts `env:NAME`:

  ```bash
  export FO_SQL='Server=sql01;Database=FlowOrchestrator;User Id=fo;Password=...;TrustServerCertificate=True'
  export FO_PG='Host=pg01;Database=flow_orchestrator;Username=fo;Password=...'
  ```

### 1. Stop new work and drain

1. Stop new triggers: disable the flows (dashboard **Disable**, `POST {base}/api/flows/{id}/disable` as an
   operator, or `IFlowStore.SetEnabledAsync`) or stop the systems that call your webhooks and manual trigger
   endpoints. Cron triggers of a disabled flow do not fire. **Write down which flows you disable here:** the
   enabled flag is copied, so they arrive disabled on the target and you re-enable them there in step 6.
2. Let the runs in status `Running` finish, or cancel them from the dashboard. A run waiting in
   `WaitForSignal`, a polling step and a step scheduled with `DelayNextStep` are all `Running` — finish or
   cancel them too.
3. Drain the runtime: on Hangfire, *Enqueued*, *Scheduled* and *Processing* must be empty. On Service Bus,
   the `flow-steps` subscriptions must be empty.
4. **Stop every host replica.** Nothing may write to the source during the copy; the verification detects
   it if something does.

### 2. Dry run

```bash
flow-orchestrator storage copy --from sqlserver --from-connection env:FO_SQL \
                               --to postgresql --to-connection env:FO_PG --dry-run
```

The dry run runs every preflight check and counts the rows per table without writing anything — not even
the target schema. It exits `3` with the reason when something would block the real copy:

| Refusal | Meaning | What to do |
|---|---|---|
| `ActiveRuns` | The source has runs in status `Running` (the message lists up to five ids) | Finish or cancel them (step 1). `--force` copies them as they are — only when the source host stays stopped for good, because the target's recovery service re-dispatches their ready steps |
| `TargetNotEmpty` | The target has rows in a FlowOrchestrator table | Use a fresh database, or `--resume` to continue an interrupted copy |
| `SourceSchemaOutdated` | The source schema predates 2.0 (lists the missing tables / columns) | Start the source host on 2.0 once, or pass `--migrate-source` |
| `SchemaNewerThanTool` | A table has a column this tool version does not know | Use the tool version that matches the hosts |
| `SourceSchemaMissing` | No FlowOrchestrator tables in the connection's default schema | Check the connection string, database and default schema |
| `SameDatabase` | Source and target are the same database | Fix the connection strings |

### 3. Copy

```bash
flow-orchestrator storage copy --from sqlserver --from-connection env:FO_SQL \
                               --to postgresql --to-connection env:FO_PG
```

The tool runs the preflight again, applies the target schema with the target's 2.0 migrator, copies every
table in primary-key order in batches (`--batch-size`, default 1000) with the backend's bulk path
(`SqlBulkCopy` into SQL Server, binary `COPY` into PostgreSQL), and then verifies. Each table is copied in
its own transaction: an interruption (Ctrl+C, a lost connection) leaves the tables before it complete and
the one in flight empty. Continue with `--resume`, which skips tables whose row count already matches the
source and copies the empty ones.

The summary lists, per table, the source rows, the copied rows, the target rows and whether it verified.
Exit code `0` means every table matched.

### 4. Verify

The copy verifies on its own: per table, the row count and an order-independent content checksum computed
by the tool from the normalized values of both sides. If time passed since the copy (or to prove it again
right before the switch), re-run only the comparison:

```bash
flow-orchestrator storage copy --from sqlserver --from-connection env:FO_SQL \
                               --to postgresql --to-connection env:FO_PG --verify-only
```

Exit code `4` names every table that differs. The usual cause is a host still writing to the source:
stop it, drop and recreate the target, and copy again.

### 5. Switch the host configuration

```diff
 builder.Services.AddFlowOrchestrator(options =>
 {
-    options.UseSqlServer(sqlConnectionString);
-    options.AddSqlServerWebhookHardening(sqlConnectionString);
+    options.UsePostgreSql(pgConnectionString);
+    options.AddPostgreSqlWebhookHardening(pgConnectionString);
     options.UseHangfire();
     // AddFlow<...>() — unchanged
 });
```

Then the runtime's storage, which the tool does **not** copy:

- **Hangfire.** Point Hangfire at storage for the new backend (for PostgreSQL, a Hangfire storage package
  such as the community `Hangfire.PostgreSql`; for SQL Server, `Hangfire.SqlServer`), or keep it on the old
  server. Because the queues were drained in step 1 there is nothing to carry over. Recurring cron jobs are
  re-registered by `FlowSyncHostedService` when the host starts, from the flow manifests plus the copied
  schedule state (cron overrides, paused triggers).
- **In-memory runtime:** nothing to do. **Service Bus:** nothing to do — the topic and queue are independent
  of the storage backend.
- **Run search token index (SQL Server target only).** Keep `UseRunSearchIndex()` if you use it: the
  index is derived data and is not copied; the host back-fills it for the copied runs in the background
  (until then deep search falls back to `LIKE`, complete but slower). PostgreSQL has no token index — its
  `pg_trgm` indexes are built by the migrator.

### 6. Start and check

1. Start **one** host. Its log shows the migrator completing (a no-op), the flows syncing and, on SQL
   Server with `UseRunSearchIndex()`, the back-fill.
2. In the dashboard: the run list and totals match the source, a few copied runs open with their steps,
   inputs and outputs, search finds copied runs, the Schedules tab shows the copied overrides and paused
   triggers, the Webhooks tab shows the copied log.
3. Trigger one run by hand and watch it complete.
4. Start the remaining replicas and re-enable, **on the new host**, the flows you disabled in step 1 (only
   those — a flow that was disabled before the migration stays disabled, and a schedule paused from the
   Schedules tab stays paused, because both are copied state).

## Rehearsal

This runbook was rehearsed end to end with the packed tool on the sample app: the sample on SQL Server
(Hangfire runtime) ran ForEach, When-skip, idempotent, WaitForSignal and webhook-rejection flows; a cron
override and a pause were set and the cron flow disabled; the host was stopped; dry run, copy and
`--verify-only` exited `0` (287 rows, 13 tables verified) and a second copy exited `3` (`TargetNotEmpty`).
Started with `FLOW_STORAGE=postgresql`, the host showed the 8 copied runs, run search and run detail
worked, the schedule kept its override (`*/7 * * * *`) and its pause, the disabled flow stayed disabled, a
trigger with a copied `Idempotency-Key` returned the copied run (`duplicate: true`) and a new run
completed. Copying that PostgreSQL database back into a fresh SQL Server database (the rollback-with-new-runs
path) verified as well (9 runs). Rehearse it once on a copy of your own data before the real window — the
dry run tells you how long the counting took, and a copy into a scratch database tells you how long the
real one will.

## Rollback

The copy never writes to the source (unless you passed `--migrate-source`, which only adds columns and
tables a 2.0 host would add anyway), so rolling back is a configuration change: stop the hosts, switch
`UsePostgreSql` back to `UseSqlServer` (and the runtime storage back), start. Runs that executed on the new
backend after the switch exist only there; to keep them, copy the new database back with the tool into a
**fresh** database (a non-empty target is refused) and point the hosts at that.

## What is copied

| SQL Server | PostgreSQL | Notes |
|---|---|---|
| `FlowDefinitions` | `flow_definitions` | including `IsEnabled` |
| `FlowScheduleStates` | `flow_schedule_states` | cron overrides, paused triggers |
| `FlowRuns` | `flow_runs` | ids, statuses, trigger data, re-run lineage; the SQL Server computed columns `IdText` / `RunSearchText` are rebuilt by the target, not copied |
| `FlowRunControls` | `flow_run_controls` | cancel / timeout state, deadlines |
| `FlowIdempotencyKeys` | `flow_idempotency_keys` | a copied key still deduplicates on the target |
| `FlowSteps` | `flow_steps` | inputs, outputs, errors, When-clause traces |
| `FlowStepAttempts` | `flow_step_attempts` | every attempt |
| `FlowOutputs` | `flow_outputs` | trigger body and headers, step inputs and outputs |
| `FlowStepDispatches` | `flow_step_dispatches` | dispatch ledger |
| `FlowStepClaims` | `flow_step_claims` | execute-once claims |
| `FlowSignalWaiters` | `flow_signal_waiters` | delivered and expired waiters |
| `WebhookReplayNonces` | `webhook_replay_nonces` | **only nonces that have not expired** |
| `WebhookRejections` | `webhook_rejections` | delivery / rejection log; ids kept, the identity / sequence moved past them |
| `FlowEvents` | `flow_events` | 1.x run-event log, **only when the source still has it** (2.0 neither creates nor reads it; kept for a 1.34 rollback). `--skip-legacy-events` leaves it behind |

The dispatch ledger and the claims are copied rather than rebuilt. Every copied run is terminal (the
preflight requires it), and for a terminal run those rows are inert — the unsent-dispatch sweep and the
recovery service only look at `Running` runs — but they are what makes a step retried from the dashboard on
the target behave exactly as it would have on the source.

**Not copied:** Hangfire's own tables, the SQL Server run-search token index (`FlowRunSearchTokens`,
`FlowRunSearchIndexState` — rebuilt by the back-fill; if the target already had the token tables, the copy
resets their back-fill state so the copied runs are indexed), expired replay nonces, and anything outside
the FlowOrchestrator tables (your own tables in the same database, such as the sample's `Orders`).

## Limits and differences between the backends

- **Timestamp precision.** SQL Server `DATETIMEOFFSET` keeps 100 ns, PostgreSQL `TIMESTAMPTZ` 1 µs. SQL
  Server → PostgreSQL truncates the seventh fractional digit (never rounds). PostgreSQL → SQL Server is
  exact. The verification compares timestamps at microsecond precision for that reason.
- **Offsets.** PostgreSQL stores instants in UTC; a SQL Server value written with a local offset (the
  stores use `SYSDATETIMEOFFSET()`, the server's offset) arrives as the same instant in UTC.
- **Text PostgreSQL cannot store.** U+0000 (NUL) and unpaired UTF-16 surrogates are valid in SQL Server
  `NVARCHAR` but not in PostgreSQL `TEXT`. The copy stops with exit code `5` naming the table, the row's
  key and the column; fix the value on the source and copy again into a fresh target.
- **Key collation (PostgreSQL → SQL Server).** SQL Server compares keys with the database collation,
  usually case-insensitive and ignoring trailing spaces; PostgreSQL compares them exactly. Two step keys
  that differ only in case collide on the SQL Server primary key; the copy stops naming the table. Create
  the target database with a case-sensitive collation (e.g. `Latin1_General_100_CS_AS_SC_UTF8`) if your
  keys need it.
- **Length of non-BMP text (PostgreSQL → SQL Server).** `VARCHAR(256)` counts characters on PostgreSQL,
  `NVARCHAR(256)` counts UTF-16 code units on SQL Server, where an emoji counts twice. A key that only fits
  on PostgreSQL is reported before the load.
- **Memory.** A batch holds at most `--batch-size` rows and stops early at about 64 MB of text, so wide
  step outputs do not multiply memory by the batch size.
- **Transaction log.** Each table is one target transaction. On SQL Server in the `FULL` recovery model a
  large `FlowSteps` table grows the log by roughly its size until the next log backup; switch the target to
  `SIMPLE` or `BULK_LOGGED` for the copy if that matters.

## Options

| Option | Meaning |
|---|---|
| `--from`, `--to` | `sqlserver` or `postgresql` (same-backend copies between two databases also work) |
| `--from-connection`, `--to-connection` | A connection string, or `env:NAME` |
| `--dry-run` | Check and count only; write nothing |
| `--verify-only` | Only compare source and target |
| `--skip-verify` | Do not verify after the copy (then run `--verify-only` before switching) |
| `--batch-size <n>` | Rows per read / bulk-load batch (1–100000, default 1000) |
| `--resume` | Continue an interrupted copy |
| `--force` | Copy runs in status `Running` as they are |
| `--migrate-source` | Upgrade an older source schema with the 2.0 migrator |
| `--skip-legacy-events` | Do not copy `FlowEvents` / `flow_events` |
| `--command-timeout <s>` | Per-command timeout (default 600, `0` = none) |
| `--verbose` / `--quiet` | Every batch plus the migrators' log / only errors and the summary |

Exit codes: `0` success · `2` usage error · `3` preflight refused · `4` verification mismatch · `5` copy
failed · `130` cancelled.

## From code

```csharp
using FlowOrchestrator.StorageMigration;

var copier = new FlowStorageCopier(new FlowStorageCopyOptions
{
    SourceProvider = FlowStorageProvider.SqlServer,
    SourceConnectionString = sqlConnectionString,
    TargetProvider = FlowStorageProvider.PostgreSql,
    TargetConnectionString = pgConnectionString,
    LoggerFactory = loggerFactory,           // optional: the copier's and the migrators' log
});

var progress = new Progress<FlowStorageCopyProgress>(p => Console.WriteLine(p.Message));
FlowStorageCopyResult result = await copier.CopyAsync(progress, cancellationToken);   // copy + verify
// later, before the switch:
await copier.VerifyAsync(progress, cancellationToken);
```

`CopyAsync` throws `FlowStoragePreflightException` (with a `Failure` code) when a rule refuses, nothing
written; `FlowStorageCopyException` when a row cannot be stored on the target; and
`FlowStorageVerificationException` (with the full `Result`) when the target does not read back equal.
