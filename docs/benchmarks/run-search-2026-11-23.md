# Run search at production scale, and what FlowEvents costs — 2026-11-23

Investigation on branch `perf/search-investigation` (from `9d13181`). The goal: make run search as
fast as possible on **Azure SQL Database** and **PostgreSQL**, and decide whether the `FlowEvents`
subsystem is worth its cost. Harness and prototypes: `tests/benchmarks/FlowOrchestrator.SearchBench`
(not in any solution filter, not in CI; README there has the run order). Raw numbers:
`tests/benchmarks/FlowOrchestrator.SearchBench/results/`.

> **Status of this document.** Both backends measured on the same data set. SQL Server: baseline, five
> candidates, per-step write cost and `FlowEvents` cost (session 1; Docker Desktop's engine failed at ~14:05 UTC
> before the token / FTS write-overhead runs). PostgreSQL: baseline, five candidates, write cost of every index
> including the migrator's `output_json` trigram index (session 2, 15:39-16:32 UTC, idle host). The 1.x
> `FlowEvents` numbers stay valid as a record; #269 has since removed persisted events on `main`.

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

## Headline (SQL Server; PostgreSQL headline below)

| Search the user runs | Shipped today (SQL Server, p50) | Best measured | Change needed |
|---|---|---|---|
| Rare word in run columns, 7-day window (`Gdpr`, `backfill`, no match) | 88-178 ms | **5-8 ms** (narrow BIN2 search column) | additive index, no write-path change |
| Same, no window | 50-827 ms | **5-79 ms** (same) | same |
| 8-char run-id prefix as shown in the run list | 68 ms in window, **not found** if older than 7 days; 0.9 s unwindowed | **6 logical reads** (`IdText` index seek, any age) | additive index |
| Deep: id / error text inside step payloads, 7-day window | **14.5 s** | **1.4 s** with no schema change (BIN2 rewrite); **0.7-2.3 ms** with the token table | query text only / opt-in table |
| Deep: same, no window | **310-334 s** (5+ minutes) | **0.8-1.2 ms** (token table) | opt-in table |
| Page 2000 of the run list | 189 ms (OFFSET) | **32 ms** (keyset) | cursor API |
| Run list total (`includeTotal=true`, sent on every page load) | 10.7 ms of a 11.9 ms page | **1.1 ms** capped count | dashboard copy ("10,000+") |

PostgreSQL, same searches (p50): quick search 1-22 ms in the window / 7-152 ms without → **~1 ms** with a trigram
search column; deep search for a rare id **18-75 ms** in the window thanks to the migrator's `output_json` trigram
index (14-15 s without it — keep it), but a common word **33 s** → **5 ms** with an `EXISTS` rewrite.

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
> them opt-in — shipped on `main` by #269, after this measurement (which ran on `9d13181`). The engine no
> longer writes them; the dashboard Events drawer and `FlowTestRunResult.Events` are derived from step
> attempts (`FlowRunTimeline`). The numbers below are the 1.x cost that decision removed.

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

## PostgreSQL results

Same data set (byte-identical rows), `postgres:18-alpine`, 10 GB container, `shared_buffers=2GB`,
`effective_cache_size=6GB`, `work_mem=64MB`, `maintenance_work_mem=1GB`. Loaded with binary `COPY`
(200k runs + 2M steps + 4.3M events in 71 s); the real migrator then built every index including the five
`pg_trgm` GIN indexes in **3 min 6 s**. TOAST compresses the JSON: `flow_steps` is **2.3 GB** (heap + TOAST)
against 11.3 GB on SQL Server, which is why the same scans are cheaper here.

Run on a healthy, otherwise idle Docker host (re-started after the SQL Server session). Two harness bugs were
found and fixed during the run and the affected steps re-measured: `VACUUM` inside a multi-statement batch
(PostgreSQL runs a batch as one transaction; the first token load built no primary key, and its timings were
discarded) and the tsvector query builder (see below). Rows marked "rows 17" instead of 16 include one run
inserted by an earlier write-cost run (its `StartedAt` is "now", inside the window).

#### PostgreSQL — quick search

| Scenario | shipped | uuid range | trigram search column | token table |
|---|---|---|---|---|
| runId prefix, recent [7d] | 21.3 / 21.9 (1) | 21.7 / 29.9 (1) | 1.00 / 1.26 (1) | 0.80 / 0.93 (1) |
| runId prefix, 60d old [7d] | 21.6 / 24.4 (0) | 22.0 / 30.3 (1) | 1.03 / 1.44 (1) | 0.76 / 0.87 (0) |
| flow name, common 'Invoice' [7d] | 1.27 / 1.75 (51) | 1.27 / 1.74 (51) | 0.95 / 1.49 (51) | 4.85 / 6.14 (51) |
| flow name, rare 'Gdpr' [7d] | 20.4 / 21.8 (16) | 20.9 / 22.4 (17) | 1.02 / 1.65 (17) | 0.93 / 1.13 (16) |
| trigger key, common 'order-created' [7d] | 1.64 / 2.42 (51) | 1.62 / 2.17 (51) | 0.99 / 1.69 (51) | 4.34 / 6.08 (51) |
| trigger key, rare 'backfill' [7d] | 16.0 / 17.9 (51) | 14.5 / 15.2 (51) | 1.98 / 2.39 (51) | 1.17 / 1.93 (51) |
| no match 'zq-nomatch-7731' [7d] | 22.0 / 23.6 (0) | 23.2 / 25.1 (0) | 0.76 / 0.84 (0) | 0.79 / 1.20 (0) |
| runId prefix, recent [all] | 151.8 / 156.7 (1) | 99.9 / 107.1 (1) | 0.95 / 1.25 (1) | 0.77 / 1.22 (1) |
| runId prefix, 60d old [all] | 107.0 / 119.4 (1) | 102.1 / 106.2 (1) | 0.98 / 1.22 (1) | 0.76 / 0.88 (1) |
| flow name, common 'Invoice' [all] | 7.27 / 7.67 (51) | 7.59 / 9.16 (51) | 0.81 / 0.89 (51) | 39.8 / 42.7 (51) |
| flow name, rare 'Gdpr' [all] | 73.8 / 78.5 (51) | 71.2 / 78.4 (51) | 1.08 / 1.24 (51) | 1.37 / 2.66 (51) |
| trigger key, common 'order-created' [all] | 7.57 / 8.36 (51) | 7.21 / 8.08 (51) | 1.05 / 1.56 (51) | 30.4 / 33.0 (51) |
| trigger key, rare 'backfill' [all] | 21.8 / 23.6 (51) | 20.7 / 22.9 (51) | 2.19 / 2.72 (51) | 2.18 / 2.35 (51) |
| no match 'zq-nomatch-7731' [all] | 108.9 / 110.7 (0) | 107.5 / 109.5 (0) | 0.75 / 0.77 (0) | 0.80 / 0.89 (0) |


- **trigram search column** — `search_text TEXT GENERATED ALWAYS AS (lower(id::text || '|' || flow_name || '|' ||
  trigger_key || '|' || status || '|' || background_job_id)) STORED` + `GIN (search_text gin_trgm_ops)`; predicate
  `search_text LIKE lower(@term)`. **~1 ms for every quick search, windowed or not**, including the 8-char run-id
  prefix at any age (the id is in the column). True substring semantics are kept — trigram answers `%term%`,
  which neither the token table nor FTS does. Cost: 46.6 MB index + 18 MB of heap on `flow_runs`; migration 3.2 s
  (the `STORED` column rewrites `flow_runs`); no measurable write cost (`StartRunAsync` p50 3.55 → 3.53 ms).
- **uuid range** — the run-id prefix as a primary-key range needs no schema at all (`bbdafe3e` →
  `bbdafe3e-0000-… .. bbdafe3e-ffff-…`), and the seek itself is free; the prototype's `IN (… UNION …)` shape kept the
  windowed text scan (100 ms unwindowed), so as on SQL Server it belongs in its own statement. With the trigram
  column it is redundant.
- **token table** — ~1 ms for rare terms; common prefixes 4-40 ms (better than SQL Server's 90 ms - 1 s for the same
  naive shape, because PostgreSQL sorts the matching token rows before touching `flow_runs`).

#### PostgreSQL — deep search

| Scenario | shipped (with output trigram) | shipped, output trigram dropped | windowed EXISTS | tsvector | token table |
|---|---|---|---|---|---|
| deep output: invoice, 3d old [7d] | 75.5 / 79.4 (1) | 14.6 s / 15.0 s / 15.1 s (n=3, 1) | 76.0 / 76.9 (1) | 79.0 / 81.4 (1) | 0.85 / 0.99 (1) |
| deep output: invoice, 60d old [7d] | 71.3 / 81.8 (0) | 14.6 s / 14.7 s / 14.8 s (n=3, 0) | 70.0 / 74.1 (0) | 77.8 / 80.3 (0) | 0.90 / 1.11 (0) |
| deep output: orderId, 3d old [7d] | 661.5 / 691.9 (1) | 14.6 s / 14.8 s / 14.8 s (n=3, 1) | 675.6 / 724.8 (1) | 288.8 / 313.8 (1) | 0.93 / 1.34 (1) |
| deep output: common 'express' [7d] | 33.1 s / 33.3 s / 35.1 s (n=3, 51) | 38.2 s / 38.3 s / 38.5 s (n=3, 51) | 5.06 / 5.26 (51) | 13.5 s / 13.8 s / 14.4 s (n=3, 51) | 0.99 / 1.42 (0) |
| deep error: shipment, 3d old [7d] | 50.7 / 57.8 (1) | 14.9 s / 16.5 s / 22.2 s (n=3, 1) | 50.6 / 55.7 (1) | 30.4 / 63.5 (1) | 1.00 / 1.16 (1) |
| deep error: 'deadlock victim' [7d] | 18.5 / 20.4 (51) | 14.3 s / 14.4 s / 14.6 s (n=3, 51) | 18.6 / 20.2 (51) | 16.7 / 23.0 (51) | 1.68 / 1.92 (51) |
| deep no match 'zq-nomatch-7731' [7d] | 25.4 / 27.3 (0) | 13.9 s / 14.0 s / 14.0 s (n=3, 0) | 25.6 / 26.4 (0) | 29.6 / 34.9 (0) | 0.91 / 1.04 (0) |
| deep output: invoice, 60d old [all] | 340.2 / 365.6 (1) | 15.0 s / 15.2 s / 15.2 s (n=3, 1) | 324.0 / 353.2 (1) | 670.1 / 899.6 (1) | 0.86 / 0.98 (1) |
| deep output: common 'express' [all] | 36.1 s / 36.3 s / 36.5 s (n=3, 51) | 38.5 s / 38.6 s / 39.6 s (n=3, 51) | 33.6 s / 33.8 s / 34.8 s (n=3, 51) | 13.1 s / 13.1 s / 15.6 s (n=3, 51) | 0.79 / 0.92 (0) |
| deep error: 'deadlock victim' [all] | 19.2 / 23.8 (51) | 14.3 s / 14.5 s / 14.5 s (n=3, 51) | 19.2 / 20.9 (51) | 14.5 / 21.4 (51) | 4.28 / 5.12 (51) |
| deep no match 'zq-nomatch-7731' [all] | 300.8 / 310.8 (0) | 14.0 s / 14.1 s / 14.1 s (n=3, 0) | 277.8 / 279.0 (0) | 298.5 / 321.8 (0) | 0.78 / 1.06 (0) |


The shipped deep query is `fr.id IN (SELECT run_id FROM flow_steps WHERE step_key ILIKE … OR error_message ILIKE … OR
output_json ILIKE …)`. Three things follow from the plans:

- **With the migrator's `output_json` trigram index, rare terms are fast**: 18-75 ms in the window, 300-340 ms
  without (a BitmapOr of the three step trigram indexes, then a recheck of the candidate payloads). The order id
  is slower (660 ms) because `ORD-2026-0193334` shares trigrams with every order id of 2026: 1,356 false-positive
  candidates, each a TOAST decompress + `ILIKE` recheck.
- **Without that index the same query is 14-15 s for every term, windowed or not** (`[7d]` = `[all]`): the `IN`
  sub-plan is evaluated over all 2M step rows before the run window applies. That is the benefit side of the
  index: **200x in the default window.**
- **Common words are pathological either way: `express` 33-36 s.** The trigram bitmap matches ~1M steps, every one
  rechecked from TOAST (240k buffers read from disk). Rewriting the `IN` to a correlated
  `EXISTS (SELECT 1 FROM flow_steps fs WHERE fs.run_id = fr.id AND (…))` lets the planner choose: for a common term
  it walks the window newest-first and stops after 60 runs (**5 ms**), for a rare term it still picks the trigram
  bitmap (same 50-75 ms; measured with the `output_json` trigram index present). Without a window the planner still mis-costs the common term (34 s), which the default
  `RunSearchWindow` already avoids.
- **tsvector** (`STORED` generated `to_tsvector('simple', step_key || error_message || output_json)` + GIN): rare
  ids 75-80 ms (no better than trigram — the run-column scan dominates), order id 289 ms, `express` 13.8 s. Cost:
  the stored vectors grow `flow_steps` from 2.3 GB to **6.0 GB** plus a 629 MB index, migration 7 min 25 s (table
  rewrite), +0.4 ms per step completion (`to_tsvector` on every payload at write time). Not worth it.
  A pitfall worth recording: the `simple` parser reads `INV-001353358` as `inv` followed by the *signed integer*
  `-001353358`, so splitting the term on `-` client-side silently matches nothing; the term has to go through
  `to_tsquery`'s own parser (`'INV-001353358':*`).
- **token table**: 0.8-1.7 ms windowed, 0.8-4.3 ms unwindowed, for ids, order ids and error words alike.

### Size, migration and write cost (PostgreSQL)

| Index / candidate | Size (200k runs / 2M steps) | Migration measured | Step-completion write cost (p50) |
|---|---|---|---|
| shipped `ix_flow_steps_output_json_trgm` | **1,138 MB** = 49% of the `flow_steps` heap | part of the 3 min 6 s post-load index build; `DROP` 0.5 s | **none measurable**: `RecordStepCompleteAsync` 4.08 ms with, 4.06 ms without |
| other shipped trigram indexes (step key, error, flow name, trigger key) | 55 + 19 + 8 + 7 MB | — | — |
| trigram search column on `flow_runs` | 46.6 MB + 18 MB heap | 3.2 s | none measurable (per run, not per step) |
| tsvector on `flow_steps` | +3.7 GB heap, +629 MB index | 7 min 25 s | +0.38 ms (4.06 → 4.44) |
| token table | **2,166 MB** (1,152 MB heap + 1,014 MB primary key — PostgreSQL has no index-organised table, so the key is stored twice) = 94% of the `flow_steps` heap | `COPY` ~30 s + primary key 10 s | 3.5 ms as a separate statement (= one commit); folded into the completion transaction, only the insert itself |

Per-step storage writes on PostgreSQL (p50 ms; commit ≈ 3 ms on this disk, round trip 0.36 ms):

| Call | shipped indexes + run search column | + token insert | − output trigram (no tokens) | − output trigram + tsvector |
|---|---|---|---|---|
| `TryRecordDispatchAsync` | 3.09 | 2.89 | 3.04 | 3.15 |
| `TryClaimStepAsync` | 3.01 | 3.03 | 2.96 | 3.03 |
| `RecordStepStartAsync` | 4.95 | 5.40 | 4.94 | 5.06 |
| event `step.started` | 3.01 | 3.05 | 2.97 | 3.03 |
| `SaveStepOutputAsync` | 3.04 | 3.07 | 2.98 | 3.07 |
| `RecordStepCompleteAsync` | **4.08** | 4.89 | **4.06** | **4.44** |
| event `step.completed` | 3.01 | 3.05 | 2.97 | 3.05 |
| `GetStepStatusesAsync` | 0.49 | 0.60 | 0.50 | 0.48 |
| token insert (separate statement) | — | 3.48 | — | — |

The "+ token insert" run was noisy (p95 of *every* call 2-4x the other runs, autovacuum on the fresh token table);
read its p50 column only. Order of the runs: column 1 → 2 (token insert added) → 3 (token insert off, output trigram dropped) → 4 (tsvector added); `run-pg-candidates.sh`. The very first shipped-only run (before the run search column existed) gave `RecordStepCompleteAsync` 4.11 ms.

`FlowEvents` on PostgreSQL: 818 MB (557 MB heap + 93 MB key + 168 MB run index) = 4.1 KB per run; 2 of 7 commits per
step (6.0 ms on 18.7 ms, +32%) — the same picture as SQL Server.

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

1. **Keep the migrator's `ix_flow_steps_output_json_trgm` as the default.** Its write cost on step completion is
   not measurable at p50 (4.08 vs 4.06 ms; GIN's pending list defers the work to vacuum), while removing it makes
   every deep search 14-15 s instead of 18-75 ms in the default window (200x). What it does cost is storage
   (1.14 GB = 49% of the `flow_steps` heap here, scaling with payload volume) and a slower post-restore / initial
   index build. Document that cost, and the knob: a host that never uses the deep-search box can drop it — that is
   an operator decision, not a default. (Caveat: measured single-writer; under many concurrent writers the GIN
   pending-list cleanup lands in whichever insert crosses `gin_pending_list_limit`, which shows up in p99, not p50
   — not measured here.)
2. **2.0.0, query text only:** turn the deep-search `fr.id IN (SELECT run_id …)` into the correlated
   `EXISTS (SELECT 1 FROM flow_steps fs WHERE fs.run_id = fr.id AND (…))` — common words 33 s → 5 ms in the window,
   rare terms unchanged (the planner still picks the trigram bitmap); and run a hex-shaped term's uuid range as its
   own statement (any-age run-id prefix with no schema).
3. **2.0.0, additive schema:** the generated `search_text` column + trigram index on `flow_runs` — every quick search
   ~1 ms, windowed or not, substring semantics preserved, 47 MB, 3.2 s to migrate, no write cost. This also replaces
   the separate `flow_name` / `trigger_key` trigram indexes, which the shipped `COALESCE(…) ILIKE` query cannot use.
4. **Keyset paging and capped count** as on SQL Server; here the absolute gains are small (page 2000: 7.9 → 1.5 ms,
   `COUNT(*)` 4.9 ms) because the run table is 93 MB.
5. **Not tsvector** (2.6x the table, 7 min migration, +0.4 ms per step, no faster than trigram for ids) and **not the
   token table by default** on PostgreSQL: trigram already answers ids in tens of ms with true substring
   semantics, while the token table costs 2.2 GB (94% of the steps heap — PostgreSQL stores the key twice) for
   0.8-4 ms. It stays the portable opt-in behind `IRunSearchIndex` for hosts that need sub-10 ms deep search on
   unwindowed history.

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
| 4. `FlowEvents` table | removed in 2.0 by #269 | — | — |

## Side findings

- `OutputJson` is written three times per step (`FlowSteps`, `FlowStepAttempts`, `FlowOutputs`). The `FlowSteps`
  clustered index alone is 11.3 GB here; production stores roughly 3x that for the same payloads.
- `FlowRuns` is clustered on a random GUID; `IX_FlowRuns_StartedAt` covering the search columns is what keeps the
  shipped search tolerable — any new run-list column must be added to it or to the new search index.
- `TriggerDataJson` (where the business id usually is) is not searched by quick or deep search today; the token
  table indexes its scalar values (source = Trigger).

## Not measured, and why

- **SQL Server write overhead of the token insert and of FTS change tracking** — the first attempt failed on a
  harness bug (reused run ids, fixed in `WriteBench`), the re-run was prevented by the Docker failure. PostgreSQL's
  token insert was measured (3.5 ms as a separate statement = one commit); expect the same shape on SQL Server.
- **Concurrent writers** — every write-cost number is a single writer. GIN pending-list cleanup and token-table
  hot spots (common tokens like `eur` take an insert per run) show up under concurrency, at p99.
- **BIN2 deep search without a window** and the BIN2 no-match row — same cause.
- **FTS indexing lag** (commit → searchable) and the `CONTAINSTABLE`-first query shape.
- **Real Azure SQL Database.** Everything ran on SQL Server 2022 in Docker. Azure SQL has FTS built in and the
  same optimizer, but fewer/slower cores (the search costs are CPU) and different commit latency (the write
  costs are commits). Ratios carry over; absolute numbers do not.
- **Managed PostgreSQL** (Azure Database for PostgreSQL flexible server): `pg_trgm` is on its allow-list but must be
  enabled per server (`azure.extensions`); the migrator already treats a missing extension as non-fatal.
- `FlowStepAttempts` / `FlowOutputs` were not seeded, so the buffer pool held more of `FlowSteps` than it would in
  production; the deep-search baseline is optimistic.

## Reproduce

See `tests/benchmarks/FlowOrchestrator.SearchBench/README.md`; `tables.py results/timings.tsv` regenerates the
tables above.


## Implementation (2.0) — shipped code, before / after (2026-11-24)

Branch `perf/2.0-run-search`. Same harness, same deterministic data set (200k runs / 1.99M steps, seed
`20261123`), same host and container settings as above, re-seeded fresh; one database container at a time,
nothing else running. **Before** = the "shipped" columns of the tables above (the 1.x query text on the 1.x
schema). **After** = the 2.0 query text exactly as the stores send it (`SqlFlowRunStore` /
`PostgreSqlFlowRunStore.BuildSearchRunsCommand`, invoked directly), on the schema the 2.0 migrators produce.
p50 ms over ≥ 20 warm runs; rows marked n=3 are the median of 3. Raw numbers:
`tests/benchmarks/FlowOrchestrator.SearchBench/results/after-2026-11-24/` (`timings.tsv`, `writes.txt`,
`ddl.tsv`, sizes, plans) and the two `after-2026-11-24-*.log` files; sequences `run-mssql-after.sh` /
`run-pg-after.sh`. Differences from the seeded data of the first session: no events table (2.0 removed it),
and a few hundred runs inserted "now" by the write-cost runs.

### SQL Server / Azure SQL

What shipped (`SqlRunSearchCommandBuilder`) and how it differs from the measured candidates:

| Shipped | Differs from the measured candidate | Why |
|---|---|---|
| Every `LIKE` is `LOWER(col) COLLATE Latin1_General_100_BIN2 LIKE @LowerLike` (run columns, step columns, legacy `GetRunsPageAsync`) | candidate `collate-deep` rewrote only the step predicates | one semantic everywhere |
| `IX_FlowRuns_StartedAt_Search (StartedAt DESC) INCLUDE (RunSearchText, FlowId, Status)`, hinted; page ids read from it, full rows by key, `ORDER BY` on the inner (index) columns | candidate included `RunSearchText` only | flow / status filters stay on the index; ordering by the outer `fr.*` columns made the optimizer sort the joined rows, whose `TriggerDataJson` sends the sort to a worktable — caught by `SqlRunSearchProductionShapeTests`, fixed |
| Id prefix: own statement first (`IdText LIKE @IdPrefix`, `@IdPrefix` as `VARCHAR`), merged in C# (`SqlGuid` order) | measured as the seek alone | the `IN (… UNION …)` shape regressed rare terms 88 → 505 ms; `VARCHAR` keeps the `CHAR(36)` seek |
| Token table: no `Source = Run` rows; insert as a separate statement **after** the step's `COMMIT` (same round trip, `sp_executesql` in `TRY…CATCH`); deep search = run columns by substring **plus** token probe, `LIKE` kept for runs the back-fill has not reached | prototype stored run tokens, was token-only, insert proposed inside the step transaction | quick search keeps substring semantics; a duplicate-key wait between concurrent steps of one run never holds the step's row locks |

**Quick search** (p50 ms)

| Scenario | before | after | |
|---|---|---|---|
| runId prefix, recent [7d] | 67.7 | **7.8** | 8.7x |
| runId prefix, 60 days old [7d] | 67.5 — **not found** | **7.2 — found** | any age |
| flow name, common 'Invoice' [7d] | 2.40 | **1.38** | |
| flow name, rare 'Gdpr' [7d] | 88.5 | **6.7** | 13x |
| trigger key, common 'order-created' [7d] | 3.37 | **1.33** | |
| trigger key, rare 'backfill' [7d] | 130.2 | **5.0** | 26x |
| no match [7d] | 178.5 | **6.5** | 27x |
| runId prefix, recent [all] | 926.0 | **83.1** | 11x |
| runId prefix, 60 days old [all] | 901.3 | **83.7** | 11x |
| flow name, common 'Invoice' [all] | 2.85 | **1.04** | |
| flow name, rare 'Gdpr' [all] | 235.5 | **22.9** | 10x |
| trigger key, common 'order-created' [all] | 3.24 | **1.07** | |
| trigger key, rare 'backfill' [all] | 49.6 | **5.1** | 10x |
| no match [all] | 826.7 | **78.6** | 11x |

The id-prefix seek itself is a few logical reads; an unwindowed id search costs what the unwindowed text
statement next to it costs (a full scan of the 51 MB narrow index, ~80 ms), since a hex term may also be in a
flow name or job id. The dashboard always windows text search (7 days by default): 7-8 ms.

**Deep search** (p50 ms; n=3 rows: median)

| Scenario | before (1.x `LIKE`) | after (BIN2 `LIKE`, default) | after (`UseRunSearchIndex()`, back-filled) |
|---|---|---|---|
| output: invoice, 3d old [7d] | 14.7 s | **1,233** | **8.3** |
| output: invoice, 60d old [7d] | 14.5 s | **1,289** | **7.5** |
| output: orderId, 3d old [7d] | 14.5 s | **1,228** | **7.9** |
| output: common 'express' [7d] | 5.36 | **1.57** | 7.5 (0 rows — prose is not tokenized) |
| error: shipment, 3d old [7d] | 14.5 s | **1,253** | **6.7** |
| error: 'deadlock victim' [7d] | 6,769 | **574** | **11.0** |
| no match [7d] | 14.3 s | **1,074** | **7.3** |
| output: invoice, 60d old [all] | 333.8 s | **98.1 s** (n=3) | **83.6** |
| output: common 'express' [all] | 6.81 | **1.95** | 89.0 (0 rows) |
| error: 'deadlock victim' [all] | 6,728 | **580** | **100.2** |
| no match [all] | 311.6 s | **98.9 s** (n=3) | **79.7** |

BIN2 is 11-12x in the window, as the candidate measured; without a window it is "only" 3.2x (334 → 98 s),
because the unwindowed run is I/O on 11.3 GB of `FlowSteps`, not CPU. With the token index the token probe is
~1 ms; the rest is the run-column statement next to it (7 ms windowed, 80 ms unwindowed).

**Listing, paging, counts** (p50 ms): page 2,000 189 (OFFSET) → **32.8** (keyset); page 50 **2.5** keyset;
`COUNT(*)` 11.0 → **1.16** capped at 10,001; full run id 0.93 → 0.59; status words unchanged (1.8-1.9).

**Migration and write cost**

| | measured |
|---|---|
| `mssql-migrate` on the loaded 200k-run database (Developer edition → online build) | startup blocked **0.22 s** (two computed columns + token tables); both indexes ready **0.92 s** later, in the background |
| sizes | `IX_FlowRuns_IdText` 13.5 MB, `IX_FlowRuns_StartedAt_Search` 51.4 MB |
| `StartRunAsync` / `CompleteRunAsync` (p50) | 7.72 / 6.23 before the migration → 6.54 / 5.51 after: **no measurable cost** of the two new indexes (run-to-run noise here ~1 ms; round trip 0.77 → 0.58) |
| `RecordStepCompleteAsync` (p50) | 6.17 → 5.83 without the token index; **11.20 with `UseRunSearchIndex()`** (+5.4 ms ≈ one more commit per step) |
| `StartRunAsync` with `UseRunSearchIndex()` | 6.54 → **11.94** (+5.4 ms, same reason) |
| token back-fill, 200k runs, batch 500 | **7 min 58 s**, 14,368,824 rows (72 / run; no run-column tokens), **1,319 MB** as built (inserted in time order, clustered by token: page splits — the prototype's bulk load + rebuild was 944 MB for 16.1M rows) |

Verdict on the token index: it does what was measured (14.5 s / 334 s → 8 / 84 ms) but **doubles the
step-completion latency** on this commit-bound disk (the separate statement is a second log flush). It stays
opt-in and experimental. Folding the insert into the step's transaction would remove the extra flush at the
price of the lock coupling described above; worth measuring under concurrent fan-out before choosing.

### PostgreSQL

What shipped (`PostgreSqlRunSearchCommandBuilder`):

| Shipped | Differs from the measured candidate | Why |
|---|---|---|
| Quick search: `lower(fr.id::text \|\| '\|' \|\| … ) LIKE @LowerLike` served by `ix_flow_runs_run_search_trgm`, a GIN trigram **expression** index, built `CONCURRENTLY` in the background, then `ANALYZE flow_runs` | candidate `trgm-run` used a `STORED` generated column + GIN on it | no table rewrite under `ACCESS EXCLUSIVE`; same speed once analyzed (below) |
| Run-id prefix: `fr.id BETWEEN @IdLow AND @IdHigh`, own statement, merged in C# (bytewise uuid order); hyphens at canonical positions | `uuid-range` inside `IN (… UNION …)` | the separate statement is the recommended shape; canonical hyphens match SQL Server's `IdText LIKE` exactly |
| Deep search: run match `OR EXISTS (SELECT 1 FROM flow_steps fs WHERE fs.run_id = fr.id AND (… ILIKE …))` | `trgm-deep-windowed`, with the new run expression | — |
| `ix_flow_steps_output_json_trgm` and the other step trigram indexes; `ix_flow_runs_flow_name_trgm` / `trigger_key_trgm` | unchanged | the step indexes are what makes deep search fast; the two run-column indexes are unused by FlowOrchestrator now but kept (documented as droppable) |
| Token table | not implemented; `UseRunSearchIndex()` logs warning 7214 | 2.2 GB per 200k runs for 0.8-4 ms against 18-75 ms |

**Quick search** (p50 ms) — including the expression-index vs stored-column decision, measured on the same
database in the same session:

| Scenario | before | after, index without statistics | **after (shipped: + `ANALYZE`)** | stored column (candidate) |
|---|---|---|---|---|
| runId prefix, recent [7d] | 21.3 | 1.28 | **1.64** | 0.99 |
| runId prefix, 60 days old [7d] | 21.6 — not found | 0.93 | **1.25 — found** | 1.28 |
| flow name, common 'Invoice' [7d] | 1.27 | 32.1 | **1.26** | 1.23 |
| flow name, rare 'Gdpr' [7d] | 20.4 | 12.2 | **1.36** | 1.19 |
| trigger key, common 'order-created' [7d] | 1.64 | 36.8 | **1.21** | 1.03 |
| trigger key, rare 'backfill' [7d] | 16.0 | 2.55 | **3.49** | 2.20 |
| no match [7d] | 22.0 | 1.07 | **1.05** | 0.75 |
| runId prefix, recent [all] | 151.8 | 1.04 | **1.28** | 0.99 |
| runId prefix, 60 days old [all] | 107.0 | 1.18 | **1.26** | 1.02 |
| flow name, common 'Invoice' [all] | 7.27 | 33.6 | **1.43** | 0.89 |
| flow name, rare 'Gdpr' [all] | 73.8 | 38.5 | **1.35** | 1.21 |
| trigger key, common 'order-created' [all] | 7.57 | 39.8 | **1.05** | 0.89 |
| trigger key, rare 'backfill' [all] | 21.8 | 2.37 | **2.78** | 2.17 |
| no match [all] | 108.9 | 0.66 | **0.74** | 0.77 |

The middle column is why the migrator now runs `ANALYZE flow_runs` after building the index: an expression
index has no statistics until then, the planner estimated 20 matches for `%invoice%` (actual 28,604) and
bitmap-scanned + sorted instead of walking `started_at` (plan in `pg-after-quick-explain.txt`). With
statistics the expression index is within ~0.1-0.5 ms of the stored column everywhere — **decision: keep the
expression index** (no table rewrite, concurrent build, no 18 MB of stored text).

**Deep search** (p50 ms; n=3: median)

| Scenario | before (`IN`) | after (`EXISTS`) |
|---|---|---|
| output: invoice, 3d old [7d] | 75.5 | **67.7** |
| output: invoice, 60d old [7d] | 71.3 | **57.0** |
| output: orderId, 3d old [7d] | 661.5 | **657.3** |
| output: common 'express' [7d] | 33.3 s | **4.5** |
| error: shipment, 3d old [7d] | 50.7 | **43.3** |
| error: 'deadlock victim' [7d] | 18.5 | **19.6** |
| no match [7d] | 25.4 | **19.8** |
| output: invoice, 60d old [all] | 340.2 | **195.5** |
| output: common 'express' [all] | 36.3 s | 34.6 s (n=3) |
| error: 'deadlock victim' [all] | 19.2 | **18.3** |
| no match [all] | 300.8 | **146.6** |

As measured on the candidate: the common word goes from 33 s to 5 ms in the window, rare terms are unchanged
or better (the run part now uses the trigram expression instead of five `COALESCE … ILIKE`), and the
unwindowed common word is still mis-costed (34.6 s) — the dashboard's default window avoids it. The order id
stays at ~660 ms (trigram false positives on `ORD-2026-…`, rechecked from TOAST).

**Listing, paging, counts** (p50 ms): page 2,000 13.8-29 (OFFSET) → **0.95** (keyset); page 50 **0.96**;
`COUNT(*)` 4.4 (the capped count stops at 10,001; not separately timed on PostgreSQL); full run id 0.42-0.67.

**Migration and write cost**

| | measured |
|---|---|
| `pg-migrate` on the loaded 200k-run database | startup blocked **0.16 s**; expression index ready **3.1 s** later, concurrently, in the background (before `ANALYZE` was added; `ANALYZE flow_runs` adds well under a second here) |
| size | `ix_flow_runs_run_search_trgm` 44.7 MB; no heap growth (the stored-column candidate adds 18 MB of heap and rewrites the table) |
| `StartRunAsync` / `RecordStepCompleteAsync` / `CompleteRunAsync` (p50) | 3.44 / 4.06 / 2.60 before → 3.46 / 4.06 / 2.63 after: **no measurable cost**; with the stored column instead: 3.43 / 4.05 / 2.65 |
