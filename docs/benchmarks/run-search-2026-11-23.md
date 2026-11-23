# Run search at production scale, and what FlowEvents costs — 2026-11-23

Investigation on branch `perf/search-investigation` (from `9d13181`). The goal: make run search as
fast as possible on **Azure SQL Database** and **PostgreSQL**, and decide whether the `FlowEvents`
subsystem is worth its cost. Harness and prototypes: `tests/benchmarks/FlowOrchestrator.SearchBench`
(not in any solution filter, not in CI; README there has the run order). Raw numbers:
`tests/benchmarks/FlowOrchestrator.SearchBench/results/`.

> **Status of this document.** SQL Server is measured: baseline, five candidates, per-step write cost
> and `FlowEvents` cost. Docker Desktop's engine failed (HTTP 500 on every API call) at ~14:05 UTC,
> before the token-table / FTS write-overhead runs and before any PostgreSQL run. PostgreSQL is
> therefore **not measured at this scale**; its recommendation leans on the 2026-05-24 trigram
> measurement and is marked as such. The PostgreSQL harness (`run-pg.sh`) is written and builds.

## Data set

Deterministic generator (`DataGenerator`, seed `20261123`), identical rows for every backend:

| | |
|---|---|
| Runs | 200,000 over 90 days ending 2026-11-23T00:00Z (~2,200/day, 15.5k in the default 7-day window) |
| Flows | 20, Zipf-like volume (OrderFulfilment 18% … GdprErasure 0.07%) |
| Step rows (`FlowSteps`) | 1,988,322 (8-12 steps per run) |
| `OutputJson` | 4.40 G chars (8.8 GB as `NVARCHAR`), avg 2.8K chars; 0.4-1.2K: 33%, 1.2-4K: 53%, 4-12K: 13%, 20-64K: 0.7% of non-null outputs; 21% of steps have no output |
| Error messages | 4.4% of step rows (failing step + "skipped because upstream failed"); 1/6 of failures carry a 1-2 KB stack trace |
| Business ids | order / invoice / shipment / payment / customer / claim ids in trigger bodies, step outputs and error text |
| `FlowEvents` | 4,304,458 rows (21.5 per run), mirroring the engine's `RecordEventAsync` calls |
| Not seeded | `FlowStepAttempts`, `FlowOutputs` (each duplicates the payload once more in production — see "Side findings") |

SQL Server: `mcr.microsoft.com/mssql/server:2022-latest` + `mssql-server-fts`
(`docker/mssql-fts/Dockerfile`), 8 GB `MSSQL_MEMORY_LIMIT_MB`, 16 vCPU host, Docker Desktop / WSL2.
`FlowSteps` clustered index after load: **11.3 GB** — larger than the buffer pool, as on a small
Azure SQL tier.

Latency = wall-clock from the .NET client through the shipped query text (`BuildSearchRunsCommand`
invoked by reflection), warm plan cache. Queries under 10 s: warm-up + ≥ 20 timed runs, p50/p95.
Queries over 10 s: **n = 3**, reported as min / median / max (the order of magnitude is the finding).
I/O from `SET STATISTICS IO`, plan shape from the actual plan.

**Contention window.** An unrelated Aspire e2e run shared the host from ~13:10 to ~13:20 UTC. Three
samples fall in it — the max of `deep … 60d old [all]`, `deep 'deadlock victim' [all]` and
`deep no match [all]`; their minimums are clean and are what the tables quote.

## Headline

| Search the user runs | Shipped today (SQL Server, p50) | Best measured | Change needed |
|---|---|---|---|
| Rare word in run columns, 7-day window (`Gdpr`, `backfill`, no match) | 88-178 ms | **5-8 ms** (narrow BIN2 search column) | additive index, no write-path change |
| Same, no window | 50-827 ms | **5-79 ms** (same) | same |
| 8-char run-id prefix as shown in the run list | 68 ms in window, **not found** if older than 7 days; 0.9 s unwindowed | **6 logical reads** (`IdText` index seek, any age) | additive index |
| Deep: id / error text inside step payloads, 7-day window | **14.5 s** | **1.4 s** with no schema change (BIN2 rewrite); **0.7-2.3 ms** with the token table | query text only / opt-in table |
| Deep: same, no window | **310-334 s** (5+ minutes) | **0.8-1.2 ms** (token table) | opt-in table |
| Page 2000 of the run list | 189 ms (OFFSET) | **32 ms** (keyset) | cursor API |
| Run list total (`includeTotal=true`, sent on every page load) | 10.7 ms of a 11.9 ms page | **1.1 ms** capped count | dashboard copy ("10,000+") |

Per step, `FlowEvents` costs **2 of the 7 commits** the storage layer performs (11.1 ms of 40.5 ms here, +37%),
5.6 KB and 21.5 rows per run, and has one reader (the dashboard's Events drawer). Recommendation:
**opt-in, off by default** in 2.0 (details below).

## Why the shipped search is slow — it is CPU, not I/O

The 2026-10-31 fix (window, `Take + 1`, no `COUNT(*) OVER()`) holds: every quick search is a range seek on
`IX_FlowRuns_StartedAt` plus a residual filter, and the deep search only reads payloads of runs in the window.
What remains is the residual itself, and it is CPU:

- Deep search, 7-day window, rare term: **0 physical reads**, 150,228 logical + 159,195 LOB logical reads,
  **CPU 14,392 ms = elapsed 14,400 ms**. The window's payloads (~2.4 GB) are fully cached; the time is the
  `NVARCHAR LIKE '%term%'` under the database's linguistic `SQL_Latin1_General_CP1_CI_AS` collation at
  ~6 ns per byte.
- Quick search, no match, 7-day window: 329 logical reads, **CPU 144 ms** — ~9 µs per run row for five
  case-insensitive comparisons, including `CAST(fr.Id AS NVARCHAR(36))` per row.
- Without a window the same CPU cost scales with history: rare deep terms 310-334 s, rare quick terms
  0.24-0.9 s. Terms with many matches stay fast (`express` 5 ms, `Invoice` 2.4 ms) because `Take + 1`
  stops the scan early. Run search is slow for exactly the searches people run in production — an order
  id, an invoice number, an error text — because those match one run.

Azure SQL tiers have fewer and slower cores than this 16-core host, so the CPU-bound numbers are lower
bounds for production.

## SQL Server results

All on the same seeded container. Cells: p50 / p95 ms (matching rows); slow rows: min / median / max (n=3).
`[7d]` = the dashboard's default `RunSearchWindow`; `[all]` = no window.

#### SQL Server — quick search (p50 / p95 ms (rows); n=3 rows: min / median / max)

| Scenario | shipped | id-prefix index (UNION shape) | narrow BIN2 search column | token table (run tokens) |
|---|---|---|---|---|
| runId prefix, recent [7d] | 67.7 / 69.4 (1) | 23.9 / 25.9 (1) | 28.8 / 30.2 (1) | 1.27 / 1.50 (1) |
| runId prefix, 60d old [7d] | 67.5 / 74.9 (0) | 23.5 / 28.0 (1) | 29.7 / 32.4 (1) | 1.15 / 1.37 (0) |
| flow name, common 'Invoice' [7d] | 2.40 / 3.07 (51) | 4.42 / 4.92 (51) | 1.36 / 1.56 (51) | 89.7 / 132.9 (51) |
| flow name, rare 'Gdpr' [7d] | 88.5 / 157.5 (16) | 505.0 / 527.8 (16) | 8.41 / 9.75 (16) | 1.40 / 2.45 (16) |
| trigger key, common 'order-created' [7d] | 3.37 / 4.66 (51) | 4.80 / 5.00 (51) | 2.10 / 2.52 (51) | 26.7 / 30.2 (51) |
| trigger key, rare 'backfill' [7d] | 130.2 / 228.7 (51) | 73.6 / 75.8 (51) | 5.36 / 7.80 (51) | 2.25 / 3.55 (51) |
| no match 'zq-nomatch-7731' [7d] | 178.5 / 258.1 (0) | 513.1 / 538.2 (0) | 6.60 / 7.26 (0) | 0.85 / 1.04 (0) |
| runId prefix, recent [all] | 926.0 / 1,482.7 (1) | 1,558.3 / 1,666.7 (1) | 108.2 / 118.8 (1) | 0.98 / 1.42 (1) |
| runId prefix, 60d old [all] | 901.3 / 1,572.4 (1) | 1,693.5 / 4,340.2 (1) | 103.4 / 106.7 (1) | 0.86 / 1.00 (1) |
| flow name, common 'Invoice' [all] | 2.85 / 3.46 (51) | 3.90 / 5.11 (51) | 1.39 / 1.69 (51) | 997.5 / 1,147.8 (51) |
| flow name, rare 'Gdpr' [all] | 235.5 / 267.1 (51) | 372.4 / 406.7 (51) | 24.9 / 31.7 (51) | 4.97 / 6.67 (51) |
| trigger key, common 'order-created' [all] | 3.24 / 3.79 (51) | 4.73 / 5.93 (51) | 1.32 / 1.78 (51) | 253.7 / 290.5 (51) |
| trigger key, rare 'backfill' [all] | 49.6 / 50.6 (51) | 74.9 / 79.9 (51) | 4.77 / 5.26 (51) | 7.08 / 9.04 (51) |
| no match 'zq-nomatch-7731' [all] | 826.7 / 869.0 (0) | — | 79.1 / 90.9 (0) | 0.68 / 0.81 (0) |


Candidate shapes (exact SQL in `MsSqlBench.Build`):

- **narrow BIN2 search column** — `RunSearchText AS CAST(LOWER(CONCAT(CONVERT(CHAR(36), Id), '|', FlowName, '|',
  TriggerKey, '|', Status, '|', BackgroundJobId)) AS NVARCHAR(800)) COLLATE Latin1_General_100_BIN2`
  (non-persisted computed column) + `IX_FlowRuns_StartedAt_Search (StartedAt DESC) INCLUDE (RunSearchText)`;
  predicate `RunSearchText LIKE lower(@term)`. One binary compare per row instead of five linguistic ones and a
  cast. Same substring semantics; case-folding is `LOWER()` instead of the collation.
- **id-prefix index** — `IdText AS CONVERT(CHAR(36), Id)` + `IX_FlowRuns_IdText`. SQL Server orders
  `uniqueidentifier` by its *last* six bytes, so a textual prefix is not a key range; an index on the text form
  is. The seek alone: **6 logical reads, 0 ms CPU** for a 60-day-old run. The prototype glued it to the text
  search with `IN (… UNION …)`, which made the optimizer give up the early-stopping scan (rare terms 88 → 505 ms):
  **ship it as a separate statement**, run first when the term is hex-shaped.
- **token table** (quick column = run-level tokens only) — exact/rare terms ~1 ms at any age; a common prefix
  (`invoice` matches 64k runs' `invoicesyncflow`) costs 90 ms / 1 s because the naive shape joins every matching
  run (257k FlowRuns reads) before sorting. A `TOP` over the token rows' own `StartedAt` before the join removes
  that; not measured.

#### SQL Server — deep search

| Scenario | shipped LIKE | BIN2 LIKE (no schema) | Full-Text Search | token table |
|---|---|---|---|---|
| deep output: invoice, 3d old [7d] | 14.6 s / 14.7 s / 14.9 s (n=3, 1) | 1,437.4 / 1,552.9 (1) | 756.3 / 849.5 / 13.3 s (n=3, 1) | 0.87 / 1.07 (1) |
| deep output: invoice, 60d old [7d] | 14.4 s / 14.5 s / 14.6 s (n=3, 0) | 1,345.9 / 1,457.8 (0) | 697.4 / 1,966.8 (0) | 0.71 / 1.09 (0) |
| deep output: orderId, 3d old [7d] | 14.0 s / 14.5 s / 14.6 s (n=3, 1) | 1,507.7 / 1,644.4 (1) | 2,221.8 / 2,302.2 (1) | 0.75 / 0.94 (1) |
| deep output: common 'express' [7d] | 5.36 / 5.97 (51) | 1.98 / 2.35 (51) | 6,679.2 / 6,906.1 (51) | 0.70 / 0.87 (0) |
| deep error: shipment, 3d old [7d] | 14.4 s / 14.5 s / 14.5 s (n=3, 1) | 1,363.1 / 1,397.8 (1) | 272.6 / 301.0 (1) | 0.72 / 0.87 (1) |
| deep error: 'deadlock victim' [7d] | 6,769.2 / 6,885.1 (51) | 627.8 / 976.0 (51) | 7,467.1 / 10.8 s (51) | 2.28 / 3.34 (51) |
| deep no match 'zq-nomatch-7731' [7d] | 14.2 s / 14.3 s / 14.4 s (n=3, 0) | — | 174.0 / 192.9 (0) | 0.76 / 0.95 (0) |
| deep output: invoice, 60d old [all] | 318.1 s / 333.8 s / 407.9 s (n=3, 1) | — | 32.0 s / 51.9 s / 60.3 s (n=3, 1) | 1.22 / 2.41 (1) |
| deep output: common 'express' [all] | 6.81 / 7.45 (51) | — | 6,590.7 / 6,898.5 (51) | 0.87 / 1.20 (0) |
| deep error: 'deadlock victim' [all] | 6,727.9 / 6,865.3 / 12.7 s (n=3, 51) | — | 7,551.5 / 12.1 s (51) | 32.0 / 42.7 (51) |
| deep no match 'zq-nomatch-7731' [all] | 310.4 s / 311.6 s / 503.2 s (n=3, 0) | — | 5,424.7 / 7,966.9 (0) | 0.79 / 0.86 (0) |


- **BIN2 LIKE, no schema change** — the shipped query with each step predicate rewritten to
  `LOWER(fs.OutputJson) COLLATE Latin1_General_100_BIN2 LIKE @lowerTerm`: **10x** across the board (14.5 s → 1.35-1.5 s;
  `deadlock victim` 6.8 s → 0.63 s). Unwindowed rows not measured (Docker failed); expect ~1/10 of 310 s.
- **Full-Text Search** — `CREATE FULLTEXT INDEX ON FlowSteps (StepKey, ErrorMessage, OutputJson LANGUAGE 0)`, neutral
  word breaker, no stoplist, `CHANGE_TRACKING AUTO`; terms as `"word*"` AND-ed. Rare ids in the window: 0.27-2.2 s.
  But: hyphenated ids are broken into very common parts (`ORD-2026-0193334` → `ord`, `2026`, `0193334`; the first
  two occur in every row) so the phrase match still scans; common words (`express`) return ~1M keys: 6.6 s against
  the shipped 5 ms; and combined with the run-column `LIKE` under `OR` the plan probes the full-text result per step
  row (`Table Spool[FulltextMatch]` under a nested loop over the window's 151k step rows) — without a window that
  became **32-60 s**. A `CONTAINSTABLE`-first shape (keys → `SearchKey` → `RunId`, merged with the run-column page
  by `StartedAt`) would avoid the probe; not measured.
- **token table** — rare ids / errors **0.7-2.3 ms in the window and 0.8-1.2 ms without one** (14.5 s → 0.9 ms;
  334 s → 1.2 ms). `deadlock victim` (two tokens, ~1% of runs) 2.3 ms / 32 ms. `express` returns **0 rows**: the
  token table deliberately indexes single-token JSON *values* (ids, codes, emails, statuses), not prose inside
  values — the semantic difference that matters.

#### SQL Server — listing, paging, counts

| Scenario | shipped | keyset | count |
|---|---|---|---|
| status word 'Failed' (+total) | 1.79 / 2.02 (51) | — | — |
| status word 'TimedOut' (+total) | 2.09 / 2.58 (51) | — | — |
| full run id (old) | 0.93 / 1.15 (1) | — | — |
| unfiltered first page (+total) | 11.9 / 15.4 (51) | — | — |
| unfiltered first page (no total) | 1.17 / 1.49 (51) | — | — |
| unfiltered page 50 OFFSET (+total) | 18.6 / 20.1 (51) | — | — |
| unfiltered page 2000 OFFSET | 189.0 / 230.5 (51) | — | — |
| unfiltered page 50 keyset | — | 2.36 / 3.01 (51) | — |
| unfiltered page 2000 keyset | — | 31.9 / 39.0 (51) | — |
| COUNT(*) FlowRuns | — | — | 10.7 / 13.0 (1) |
| COUNT(*) WHERE Status='Failed' | — | — | 1.14 / 1.40 (1) |
| capped COUNT (TOP 10001) | — | — | 1.13 / 1.31 (1) |
| dm_db_partition_stats row_count | — | — | 0.56 / 0.66 (1) |


- `OFFSET` cost grows with the page number (page 2000: 189 ms); a `(StartedAt, Id)` keyset cursor is 32 ms there and
  2.4 ms at page 50 (the `OR`-form predicate still scans part of the index; the row-value form PostgreSQL has
  would be a pure seek).
- The dashboard sends `includeTotal=true` on every run-list load: `COUNT(*)` is 10.7 ms of the 11.9 ms page at
  200k runs, and grows linearly with retention. A capped count (`TOP (10001)`) is 1.1 ms and enough for "10,000+";
  `sys.dm_db_partition_stats` is 0.56 ms but needs `VIEW DATABASE STATE`, which an app login on Azure SQL often
  lacks.

### Size, migration and write cost (SQL Server)

| Candidate | New storage (200k runs / 2M steps) | Migration measured here | Write path |
|---|---|---|---|
| BIN2 rewrite | none | none | none |
| narrow search column | 43.1 MB index (`FlowRuns` total 329 MB) | 0.4 s (metadata column + index build) | index maintained on `StartRunAsync` / status updates — one 200-byte row per run, not per step |
| `IdText` index | 13.0 MB | 0.5 s | same, per run |
| token table | **943.6 MB** (16.1M rows: 81/run — 9 run, 19 trigger, 26 step-key, 2 error, 24 payload) = 8% of `FlowSteps` | backfill 6 min 41 s (bulk, from regenerated data) | ~5.3 tokens per step completion + ~28 per run start; **not measured** — see below |
| Full-Text Search | **2,393 MB** catalog + 115.6 MB unique key index + 8 B/row identity column = 22% of `FlowSteps` | identity column 28 s, full population **9 min 28 s** for 1.99M rows | change tracking is asynchronous (commit cost small, search lags by the crawl); **not measured** |

Per-step storage writes today (the engine's real store methods on the seeded tables; p50 ms; 1,935 steps):

| Call | p50 | p95 |
|---|---|---|
| round trip (`SELECT 1`) | 0.73 | 1.43 |
| `TryRecordDispatchAsync` | 5.56 | 8.70 |
| `TryClaimStepAsync` | 5.36 | 8.88 |
| `RecordStepStartAsync` | 5.90 | 9.03 |
| **event `step.started`** | **5.46** | 8.57 |
| `SaveStepOutputAsync` | 5.87 | 9.16 |
| `RecordStepCompleteAsync` | 6.00 | 9.37 |
| **event `step.completed`** | **5.60** | 8.66 |
| `GetStepStatusesAsync` | 0.78 | 1.35 |
| per run: `StartRunAsync` 6.64, `CompleteRunAsync` 5.57, events `run.started` 5.53 + `run.completed` 5.33 | | |

Every write is ~5 ms above the 0.7 ms round trip: that is the commit (log flush) on this Docker/WSL2 disk. Azure
SQL's commit latency is tier-dependent (General Purpose writes the log to remote storage), so read the table as
*number of commits per step*: **7, of which 2 are events.** A search-token insert is a separate statement only in
the prototype; folded into `RecordStepCompleteAsync`'s existing transaction it adds no commit — only the
server-side insert of ~5 B-tree rows. That folded cost is the number still to measure before shipping.

## FlowEvents — keep, opt-in, or remove

> **Decision (2026-11-23):** the maintainer chose to **remove** persisted events in 2.0 rather than make
> them opt-in. The engine no longer writes them; the dashboard Events drawer and `FlowTestRunResult.Events`
> are derived from step attempts (`FlowRunTimeline`). The verdict below is the measurement-time proposal.

**Cost (measured, SQL Server):**

| | |
|---|---|
| Rows | 4,304,458 for 200k runs = **21.5 per run** (run.started, step.started + step.completed/failed per step, step.skipped, run.completed) |
| Size | 839.8 MB clustered + 273.0 MB `IX_FlowEvents_RunId_Sequence` = **1,113 MB = 5.6 KB per run** — 3.4x all of `FlowRuns` (329 MB with indexes) |
| Write path | 2 commits per step + 2 per run, each ~5.5 ms here = **+37% on per-step storage time** (11.1 ms on 29.5 ms) |
| Where | `step.started` is awaited before the handler runs, `step.completed` before the continuation (`FlowOrchestratorEngine.Step.cs:104, :196`) — on the step's critical path, not background |
| Failure mode | best-effort (exceptions swallowed), so it can only add latency, never correctness |

**Readers** (complete list from the code):

1. Dashboard run detail → *Events* drawer (`GET /flows/api/runs/{runId}/events`, `dashboard.js loadEventsIntoDrawer`) —
   one of four run views (timeline / DAG / Gantt / events); it already renders "Event stream not enabled" when empty.
2. `FlowOrchestrator.Testing` — `FlowTestRunResult.Events`, read from the **in-memory** store; the test host forces
   `EnableEventPersistence = true` (its doc comment claims the default is off — it is `true`).
3. Nothing else. The SSE live stream (`IFlowEventNotifier` / `SseFlowEventBroadcaster`) is a separate, in-process,
   non-persisted channel and is unaffected. Retention deletes `FlowEvents` with the run.

**Where the same information already lives:**

| Event | Already in |
|---|---|
| `run.started` / `run.completed` (+status) | `FlowRuns.StartedAt` / `CompletedAt` / `Status` |
| `step.started` / `step.completed` / `step.failed` (+error) | one `FlowStepAttempts` row per execution: `StartedAt`, `CompletedAt`, `Status`, `ErrorMessage` |
| `step.pending` (poll / WaitForSignal park) | one attempt row per invocation; only the `pending for {delay}` value is unique to the event |
| `step.skipped` (when-clause false) | `FlowSteps.EvaluationTraceJson` |
| `step.skipped` (dependencies did not match / run cancelled) | `FlowSteps.Status = Skipped` + DAG + `FlowRunControls` — derivable, reason text not stored |
| all of the above, with timing | OpenTelemetry spans / metrics when `EnableOpenTelemetry` |

**Verdict: opt-in, off by default (2.0).** Set `FlowObservabilityOptions.EnableEventPersistence` default to `false`
on the SQL Server / PostgreSQL backends; render the Events drawer from `FlowStepAttempts` when no event rows exist
(the data is all there except the pending delay and skip-reason text, which can go into the attempt row's
`ErrorMessage` / a reason column); keep it on in `FlowOrchestrator.Testing` (in-memory, free) and fix that doc
comment. Not *remove* yet: hosts that ship the event table to an audit pipeline lose nothing by opting in, and the
table can be dropped in a later major once the drawer no longer needs it. If it stays on for someone, make the two
step events part of the step's existing transactions instead of separate commits.

## PostgreSQL (not measured at this scale)

What is known, from code and the 2026-05-24 measurement (100k runs x 5 steps, PostgreSQL 16):

- Deep search uses `fr.id IN (SELECT run_id FROM flow_steps WHERE step_key ILIKE … OR error_message ILIKE … OR
  output_json ILIKE …)` with bare columns, which the migrator's `pg_trgm` GIN indexes serve (BitmapOr of three
  index scans: step side 310 → 9 ms then). That rewrite is shipped.
- Quick search still wraps the run columns in `COALESCE(…) ILIKE`, which no index can serve, and `id::text`,
  `status`, `background_job_id` have no trigram index, so the `OR` cannot become a BitmapOr anyway. The prototype
  `trgm-run` (one generated lower-cased `search_text` column + GIN trigram) is the PostgreSQL analogue of the SQL
  Server narrow column; it is written but not measured.
- A run-id prefix needs **no schema** on PostgreSQL: `uuid` compares bytewise, so `bbdafe3e` is the primary-key range
  `bbdafe3e-0000-… .. bbdafe3e-ffff-…` (`PgBench.UuidRange`).
- Unknown and important: the size and **write cost of `ix_flow_steps_output_json_trgm`** at 4.4 G chars of output
  (a trigram GIN on large JSON is typically larger than the column and is maintained on every step completion).
  That index is created by the migrator today on every PostgreSQL install. `run-pg.sh` measures it with and
  without (`pg-write without-output-trgm`). **This is the one run essential to the PostgreSQL recommendation.**

## Recommendation

### SQL Server / Azure SQL Database

1. **2.0.0, query text only:** rewrite every `LIKE` in `SqlFlowRunStore.BuildSearchRunsCommand` to
   `LOWER(col) COLLATE Latin1_General_100_BIN2 LIKE @lowerTerm` (term lower-cased once in C#). Deep 10x
   (14.5 s → 1.4 s), quick search the same mechanism. Semantics: still case-insensitive substring; differs from
   the database collation only for linguistic equivalences (e.g. `ß` vs `ss`) which `CI_AS` treats as equal.
2. **2.0.0, additive schema (seconds to migrate):** `RunSearchText` computed column + `IX_FlowRuns_StartedAt_Search`
   (43 MB per 200k runs) and `IdText` + `IX_FlowRuns_IdText` (13 MB). Quick search 10-30x; an 8-char id from the run
   list is found at any age in one seek, run as its own statement before the text search.
3. **2.0.0, API:** keyset cursor on `RunSearchQuery` (`After = (StartedAt, Id)`) next to `Skip`; dashboard asks for
   a capped total (`TotalAtMost = 10_000`) instead of an exact `COUNT(*)`.
4. **Opt-in (`UseRunSearchIndex()`): the token table for deep search.** It is the only measured option that answers
   "find the run for invoice INV-001353358" in milliseconds regardless of window (0.7-2.3 ms vs 14.5 s / 334 s), it is
   plain T-SQL (works on every Azure SQL tier, no FTS feature, no extension), and its size is bounded by policy (81
   tokens/run, 8% of `FlowSteps`). Ship with the token insert folded into the step-completion transaction, the
   `TOP`-over-tokens query shape for common prefixes, `IGNORE_DUP_KEY` (or `ON CONFLICT DO NOTHING`) for concurrent
   steps of one run, and a resumable backfill.
5. **Full-Text Search: not the default.** Available natively on Azure SQL, but on this data it is 22% extra storage,
   a 9.5-minute population per 2M steps, asynchronous indexing (a just-failed run is not immediately findable),
   word-breaker splits that make the most common search (hyphenated business ids) slow, and plans that collapse
   without a window. If a host wants word search over prose, it should get it through the `IRunSearchIndex` seam
   (FTS, Azure AI Search, Elasticsearch), not as a built-in.

### PostgreSQL

Pending the `run-pg.sh` measurement: same structure — generated `search_text` + GIN trigram for quick search (keeps
true substring semantics, which trigram supports and the token table does not), uuid-range id prefix (no schema),
keyset + capped count, and the token table as the opt-in deep-search index. Decide on keeping the migrator's
`output_json` trigram index by default only after its write cost is measured; if it is material, move it behind
the same opt-in.

### The seam

`tests/benchmarks/FlowOrchestrator.SearchBench/Prototype/IRunSearchIndex.cs` (compiled, not shipped):
`Capabilities` (substring / token-prefix / errors / payloads / ordered-by-start) so the dashboard labels the box
honestly; `IndexRunAsync` / `IndexStepAsync` called after the durable writes with `CancellationToken.None`,
best-effort like event persistence; `SearchAsync` returns run ids newest-first with a limit; `RemoveRunsAsync` for
retention. `SearchRunsAsync` hydrates the page with a primary-key `IN`. Built-ins: LIKE/BIN2 (default), token table
(opt-in), trigram (PostgreSQL); external engines plug in by implementing it.

### Migration plan for 2.0

| Step | Schema | Backfill | Flag |
|---|---|---|---|
| 1. BIN2 / no-COALESCE query text, capped count, keyset cursor, events default off | none | none | `EnableEventPersistence` default flips to `false` (CHANGELOG "Read before upgrading") |
| 2. Search column + `IdText` (SQL Server); `search_text` + GIN (PostgreSQL) | additive, idempotent in the migrators; SQL Server: computed column + index (seconds); PostgreSQL: a `STORED` generated column rewrites `flow_runs` (small table) | none — computed / generated | always on |
| 3. `FlowRunSearchTokens` + `IRunSearchIndex` | new table, created only when opted in | hosted service, batches by `StartedAt DESC` so recent runs become searchable first; resumable via a watermark row; ~6.7 min per 200k runs measured as a bulk load | `options.UseRunSearchIndex()` |
| 4. `FlowEvents` table | kept; dropped in a later major once the drawer reads attempts | — | — |

## Side findings

- `OutputJson` is written three times per step (`FlowSteps`, `FlowStepAttempts`, `FlowOutputs`). The `FlowSteps`
  clustered index alone is 11.3 GB here; production stores roughly 3x that for the same payloads.
- `FlowRuns` is clustered on a random GUID; `IX_FlowRuns_StartedAt` covering the search columns is what keeps the
  shipped search tolerable — any new run-list column must be added to it or to the new search index.
- `TriggerDataJson` (where the business id usually is) is not searched by quick or deep search today; the token
  table indexes its scalar values (source = Trigger).

## Not measured, and why

- **PostgreSQL, everything** at this scale — Docker Desktop's engine failed before the PostgreSQL run; `run-pg.sh`
  (~1.5-2 h incl. the trigram index build on 4.4 G chars) is ready. Essential for the PostgreSQL recommendation.
- **Write overhead of the token insert and of FTS change tracking** — the runs were scheduled after the candidate
  queries; the first attempt failed on a harness bug (reused run ids, fixed in `WriteBench`), the re-run was
  prevented by the Docker failure.
- **BIN2 deep search without a window** and the BIN2 no-match row — same cause.
- **FTS indexing lag** (commit → searchable) and the `CONTAINSTABLE`-first query shape.
- **Real Azure SQL Database.** Everything ran on SQL Server 2022 in Docker. Azure SQL has FTS built in and the
  same optimizer, but fewer/slower cores (the search costs are CPU) and different commit latency (the write
  costs are commits). Ratios carry over; absolute numbers do not.
- `FlowStepAttempts` / `FlowOutputs` were not seeded, so the buffer pool held more of `FlowSteps` than it would in
  production; the deep-search baseline is optimistic.

## Reproduce

See `tests/benchmarks/FlowOrchestrator.SearchBench/README.md`; `tables.py results/timings.tsv` regenerates the
tables above.

## Implementation (2.0, SQL Server) — what shipped and what is still to measure

Branch `perf/2.0-run-search`. Everything above was measured on **prototype** query text
(`MsSqlBench.Build`). The shipped text (`SqlRunSearchCommandBuilder`) follows the winning candidates,
with these differences — each needs the re-measurement listed below before its number is quoted as
"after":

| Shipped | Differs from the measured candidate | Why |
|---|---|---|
| Every `LIKE` is `LOWER(col) COLLATE Latin1_General_100_BIN2 LIKE @LowerLike` (run columns, step columns, legacy `GetRunsPageAsync`) | candidate `collate-deep` rewrote only the step predicates | one semantic everywhere |
| `IX_FlowRuns_StartedAt_Search (StartedAt DESC) INCLUDE (RunSearchText, FlowId, Status)` | candidate included `RunSearchText` only | the index is hinted, so a flow / status filter must not cost a key lookup per scanned row |
| Id prefix: own statement first, merged in C# (`SqlGuid` order) | measured as the seek alone (6 logical reads), not as a two-statement batch | the measured `IN (… UNION …)` shape regressed rare terms 88 → 505 ms |
| `@IdPrefix` sent as `VARCHAR` | prototype sent `NVARCHAR` | `IdText` is `CHAR(36)`; an `NVARCHAR` parameter converts the column and can turn the seek into a scan under a `SQL_*` collation |
| Keyset `ORDER BY StartedAt DESC, Id` with the `OR` predicate | same as measured (`mssql-keyset`) | — |
| Capped count `COUNT(*)` over `TOP (cap + 1)` | same as measured | — |
| Token table: no `Source = Run` rows (run columns are matched by `RunSearchText`) | prototype stored 9 run tokens per run | saves ~11% of the table; quick search keeps substring semantics |
| Token insert: separate statement **after** the step's `COMMIT`, same round trip, `sp_executesql` in `TRY…CATCH` | doc proposed folding it into the step transaction | with `IGNORE_DUP_KEY`, concurrent steps of one run inserting the same token wait on each other's key locks; inside the step transaction that wait would hold the step's row locks. Cost: one extra log flush per step completion, **not measured** |
| Deep search with tokens: run columns by substring **plus** token probe; `LIKE` probe kept for runs at or before the back-fill cursor | prototype was token-only | results stay a superset of quick search and complete while the back-fill runs |

### Still to measure (needs Docker; harness commands)

```bash
cd tests/benchmarks/FlowOrchestrator.SearchBench
dotnet run -c Release -- mssql-seed 200000                  # if the container is new; BEFORE = the "shipped" columns above
dotnet run -c Release -- mssql-migrate tokens               # shipped migrator: startup-blocked time + online index build time
dotnet run -c Release -- mssql-base quick                   # AFTER: quick search, id prefix, capped count (keyset text = mssql-keyset)
dotnet run -c Release -- mssql-base deep                    # AFTER: deep search with BIN2 LIKE (token index not used by this path)
dotnet run -c Release -- mssql-backfill 500                 # shipped back-fill duration + token rows at 200k runs
dotnet run -c Release -- mssql-write baseline 200           # per-step write cost without the token index
dotnet run -c Release -- mssql-write tokens 200 shipped-tokens   # same with UseRunSearchIndex(): compare StartRunAsync / RecordStepCompleteAsync
```

`mssql-base` calls `SqlFlowRunStore.BuildSearchRunsCommand`, which builds against the complete schema;
after `mssql-migrate` it measures exactly what the store sends. Not measured at all yet: deep search
through the token table **as composed by the store** (`SqlFlowRunStore.SearchRunsAsync` with the token
index), the `TOP`-over-tokens shape for very common prefixes, and the orphan sweep at 16M token rows.
