# Deep RUN search on SQL Server & PostgreSQL — measured investigation, 2026-05-24

Quantifies the cost of **deep** RUN search (`IFlowRunStore.GetRunsPageAsync(..., deepSearch: true, ...)`)
on the two SQL backends and pinpoints the bottleneck with actual query plans.
Companion to `run-search-quick-vs-deep-2026-05-24.md`, which characterised the
in-memory store and explicitly left the SQL backends out of scope. This doc fills
that gap with Testcontainers-backed measurements against the **real migrators**.

> Investigation only — no library source was modified. The harness lived in a
> throwaway `.investigation/SqlDeepSearchProbe` project (not part of the solution
> or CI) and was deleted after capture. Reproduction notes at the end.

## What the store actually issues

Both stores build the same shape (`SqlFlowRunStore.GetRunsPageAsync`,
`src/FlowOrchestrator.SqlServer/SqlFlowRunStore.cs:137`;
`PostgreSqlFlowRunStore.GetRunsPageAsync`,
`src/FlowOrchestrator.PostgreSQL/PostgreSqlFlowRunStore.cs:148`):

```
SELECT fr.*, COUNT(*) OVER() AS TotalCount
FROM FlowRuns fr
WHERE ( identity-column LIKE/ILIKE '%term%'           -- id, flow name, trigger key, status, job id
        OR EXISTS (SELECT 1 FROM FlowSteps fs           -- DEEP only
                   WHERE fs.RunId = fr.Id
                     AND ( ISNULL/COALESCE(fs.StepKey,'')    LIKE/ILIKE '%term%'
                           OR ISNULL/COALESCE(fs.ErrorMessage,'') LIKE/ILIKE '%term%'
                           OR ISNULL/COALESCE(fs.OutputJson,'')   LIKE/ILIKE '%term%' )) )
  [AND fr.FlowId = @FlowId]            -- when the dashboard passes ?flowId=
  [AND fr.StartedAt BETWEEN ... ]      -- when the dashboard passes ?from= / ?to=
ORDER BY fr.StartedAt DESC OFFSET/FETCH (or LIMIT/OFFSET)
```

SQL Server wraps each step column in `ISNULL(col,'')`; PostgreSQL wraps in
`COALESCE(col,'')`. **That wrapper is the single most important detail in this
whole investigation** (see PostgreSQL §).

The `/flows/api/runs` endpoint
(`src/FlowOrchestrator.Dashboard/DashboardServiceCollectionExtensions.cs:587`)
passes `flowId`, `status`, `from`, `to`, and `deep` straight through, so the
**bounding levers below are reachable today** from the dashboard query string
(`?flowId=`, `?from=`, `?to=`).

## Setup

- **100,000 runs × 5 steps = 500,000 step rows.** Each step's `OutputJson`/`output_json`
  is a ~300-byte JSON blob. The search needle (`needle-7f3a9c2e`) is planted in
  exactly **one** step's output for **50** runs (every 2,000th run) and in **no**
  identity column — so the quick path is a true negative (scans, 0 matches) and
  the deep path must scan every step row and matches 50.
- `take: 20` (dashboard default), `skip: 0`.
- Seeded via binary `COPY` (PG) / `SqlBulkCopy` (SQL Server); `ANALYZE` /
  `UPDATE STATISTICS` run before measuring so the planners have fresh stats.
- Real migrators: `PostgreSqlFlowOrchestratorMigrator` (creates the `pg_trgm` GIN
  indexes) and `FlowOrchestratorSqlMigrator`.
- Latency = median of 7 timed runs (2 warmup), wall-clock from the .NET client.
- postgres:16-alpine and mssql/server:2022-latest in Docker (WSL2), 16 GB host.

Trigram indexes confirmed present on PG after migration:
`ix_flow_steps_output_json_trgm`, `_error_message_trgm`, `_step_key_trgm`,
`ix_flow_runs_flow_name_trgm`, `_trigger_key_trgm`.

---

## Measured latency (median of 7, ms)

| Backend | quick unbounded | deep unbounded (store SQL) | deep unbounded (rewrite) | quick bounded(flowId) | deep bounded(flowId) (store SQL) | deep bounded (rewrite) |
|---|---:|---:|---:|---:|---:|---:|
| **PostgreSQL** | 140.4 | **462.1** | **151.5** | 22.3 | **344.9** | **35.4** |
| **SQL Server** | 343.8 | **7,908.2** | n/a (no trigram) | 46.4 | **991.1** | n/a |

Headline reads:

- **PostgreSQL deep is 3.3× the quick path** (462 vs 140 ms) unbounded — but a
  one-line query rewrite that lets the GIN trigram index engage cuts deep to
  **151 ms (3.0×)**, i.e. essentially the quick-path cost plus a 9 ms indexed
  step lookup.
- **SQL Server deep is 23× the quick path** (7.9 s vs 344 ms) unbounded, because
  the step LIKE is an un-indexable residual scan over all 500k step rows, run as
  a semi-join (`FlowSteps` logical reads = 500,497, CPU = 7,805 ms — see SQL
  Server §). There is no trigram/FTS to rescue it (FTS rejected by the team), so
  the realistic lever is **bounding**: a `flowId` filter alone takes deep from
  **7.9 s → 1.0 s (≈8×)** by cutting scanned step rows 8×.
- Bounding helps the **quick** tier on both backends too (PG 140→22 ms; SQL
  Server 344→46 ms) — the identity-column LIKEs are themselves non-sargable, so
  narrowing the run set first is the dominant win regardless of tier.

> SQL Server numbers are from the second probe run (with STATISTICS IO/XML capture
> enabled); they agree with the first run within noise (deep unbounded 7.41 vs 7.91
> s — n=7 medians, the 300-byte LIKE residual has run-to-run variance, the order of
> magnitude and the IO counts are the signal).

---

## PostgreSQL — the key question: is the `pg_trgm` GIN index used?

**No — the store's SQL prevents it, and the cause is the `COALESCE` wrapper, not
the correlated `EXISTS`.** This is provable from three plans.

### 1. The store's deep query (correlated EXISTS + COALESCE) → Parallel Seq Scan

`EXPLAIN (ANALYZE, BUFFERS)` of the actual deep-unbounded SQL:

```
Index Scan using ix_flow_runs_started_at on flow_runs fr  (actual time=314..450 rows=50)
  Filter: ( fr.id::text ~~* '%needle%'  OR ... OR (hashed SubPlan 2) )
  Rows Removed by Filter: 99950
  SubPlan 2
    -> Gather (Workers Launched: 2)
       -> Parallel Seq Scan on flow_steps fs  (actual time=40..311 rows=17 loops=3)
            Filter: ( COALESCE(fs.step_key,'') ~~* '%needle%'
                      OR COALESCE(fs.error_message,'') ~~* '%needle%'
                      OR COALESCE(fs.output_json,'') ~~* '%needle%' )
            Rows Removed by Filter: 166650
Execution Time: 450.761 ms
```

Two things happen:
- The planner **de-correlates the `EXISTS` itself** into a one-time hashed SubPlan
  (good — it does *not* re-run per outer row).
- But that SubPlan is a **Parallel Seq Scan of all 500k step rows** with the
  `COALESCE(...) ~~* '%needle%'` filter. The GIN trigram index is **not** touched.

### 2. The de-correlated `IN` rewrite (no COALESCE) → BitmapOr across all three GIN indexes

Rewriting the step match as a pre-filtered `IN (SELECT run_id FROM flow_steps WHERE
step_key ILIKE ... OR error_message ILIKE ... OR output_json ILIKE ...)` — crucially
**dropping the `COALESCE`** — changes the plan completely:

```
SubPlan 1
  -> Bitmap Heap Scan on flow_steps fs  (actual time=8.9..9.0 rows=50)
       -> BitmapOr
            -> Bitmap Index Scan on ix_flow_steps_step_key_trgm
            -> Bitmap Index Scan on ix_flow_steps_error_message_trgm
            -> Bitmap Index Scan on ix_flow_steps_output_json_trgm  (rows=50)
Execution Time: 157.556 ms
```

The step-side cost collapses from ~310 ms (parallel seq scan) to **9 ms** (three
indexed bitmap scans OR-ed). The remaining 150 ms is now the **run side** — the
`ix_flow_runs_started_at` Index Scan still filters 99,950 rows for the
identity-column ILIKEs (those are also non-sargable; see "remaining cost" below).

### 3. Proof the COALESCE is the blocker (not term length, not ILIKE)

Isolating the bare predicate, with `enable_seqscan = off` to force the planner's
hand:

```
-- bare:  output_json ILIKE '%needle%'           (NO coalesce)
Bitmap Heap Scan ... -> Bitmap Index Scan on ix_flow_steps_output_json_trgm
Execution Time: 1.492 ms          ← trigram index works perfectly for ILIKE

-- store's expr:  COALESCE(output_json,'') ILIKE '%needle%'   with seqscan OFF
Seq Scan on flow_steps  (cost=10000000000.00..) Rows Removed by Filter: 499950
Execution Time: 2021.127 ms       ← index UNUSABLE even when seqscan is penalised
```

The `cost=10000000000` is the artificial penalty `enable_seqscan=off` applies —
the planner picks a seq scan *anyway* because **a plain-column `gin(output_json
gin_trgm_ops)` index cannot satisfy an expression `COALESCE(output_json,'')`.**
The expression is opaque to the index. `pg_trgm` fully supports `ILIKE` (`~~*`);
the case-insensitivity and the term are not the problem — the `COALESCE` wrapper is.

### Why the store wraps in COALESCE at all

`COALESCE(col,'')` makes a NULL column compare as empty string so the OR-chain
doesn't short-circuit to NULL. It's defensive, but **unnecessary for `ILIKE`**:
`NULL ILIKE '%x%'` is already `NULL`, which is falsy in a `WHERE`/`OR` context, so
`col ILIKE '%term%'` alone yields identical results to `COALESCE(col,'') ILIKE
'%term%'` for the purpose of "does any row match". Dropping the wrapper is
behaviour-preserving for the search semantics and is what makes the index usable.

### PostgreSQL — ranked recommendations

1. **(Biggest single win, low risk) Drop `COALESCE` from the step ILIKE terms and
   de-correlate into a pre-filtered `IN (SELECT run_id ...)`.** Measured: deep
   unbounded **462 → 151 ms (3.05×)**; the step-side work drops from ~310 ms to
   **9 ms**. The trigram GIN indexes the migrator already creates finally do their
   job. Both edits are required: de-correlation alone keeps the seq scan if the
   COALESCE stays (the `IN` body would still be `COALESCE(...) ILIKE`).
   File: `src/FlowOrchestrator.PostgreSQL/PostgreSqlFlowRunStore.cs:176-202`.

2. **(Removes the residual run-side cost) Also drop `COALESCE` from the
   identity-column ILIKEs and add trigram indexes (or de-correlate) for them.**
   After fix #1 the dominant 150 ms is the `ix_flow_runs_started_at` scan filtering
   100k rows for `COALESCE(flow_name,'') ILIKE`, etc. `flow_name` and `trigger_key`
   already have trigram indexes (`ix_flow_runs_flow_name_trgm`,
   `_trigger_key_trgm`) but the `COALESCE` wrapper blocks them exactly as it does on
   the step side. Dropping it would let those engage; `status`, `id::text`, and
   `background_job_id` have no trigram index and would remain residual (they are
   short/low-cardinality, so cheaper). Expect deep unbounded to approach the
   indexed step cost (~tens of ms).

3. **(Free, already wired) Encourage bounding from the UI.** `?flowId=` takes deep
   from 462 → 345 ms even *without* the rewrite (and 151 → 35 ms *with* it);
   `?from=/?to=` similarly bounds via `ix_flow_runs_started_at`. The dashboard
   already forwards these. A default date window on the runs list (e.g. last 7
   days) would cap worst-case latency on large histories.

4. **(Doc fix) `docs/articles/dashboard.md:153` claims "On PostgreSQL these
   substring searches are accelerated by `pg_trgm` GIN indexes."** That is **not
   true today** for the deep step search — measured. Either land fix #1 (making the
   statement true) or correct the doc.

---

## SQL Server — where is the cost?

SQL Server has **no trigram and no Full-Text index** here (FTS was rejected by the
team earlier). A `LIKE '%term%'` with a leading wildcard is **non-sargable** — no
B-tree can seek it — so it is always a residual scan. The only question is *how
much* gets scanned, and the answer is "everything" unless a bounding predicate
narrows it first.

### Deep unbounded — 7.9 s

`SET STATISTICS IO, TIME ON` on the actual deep-unbounded query:

```
Table 'FlowSteps'.  Scan count 100000, logical reads 500497, lob logical reads 0
Table 'FlowRuns'.   Scan count 1,      logical reads 2061
Table 'Worktable'.  Scan count 3,      logical reads 105
   CPU time = 7805 ms,  elapsed time = 7807 ms.
```

Actual plan (`SET STATISTICS XML ON`), key operators:

```
Top → Nested Loops (Inner Join) → ... → Nested Loops (Left Semi Join)
  ├─ Index Scan  Object=[FlowRuns].[IX_FlowRuns_StartedAt]   (for the ORDER BY)
  └─ Clustered Index Seek  Object=[FlowSteps].[PK_FlowSteps] + residual LIKE Filter
max ActualRows across operators: 500000
```

The shape: walk `FlowRuns` in `StartedAt` order (`IX_FlowRuns_StartedAt`), and for
each run run a `Left Semi Join` that seeks `PK_FlowSteps` by `RunId` and applies
`ISNULL(OutputJson,'') LIKE '%needle%'` as a **residual `Filter`**, not a seek
predicate — the definition of non-sargable. The damning numbers:

- **`FlowSteps` Scan count = 100,000** (one probe per run) and **logical reads =
  500,497** — i.e. **every one of the 500k step rows is read and LIKE-tested.**
  `max ActualRows = 500000` confirms the residual touches the whole step table.
- **CPU time = 7,805 ms** — almost all of it is the per-row `LIKE` on a ~300-byte
  string × 500k rows. This is pure non-sargable residual cost.

### Deep bounded(flowId) — 1.0 s (≈8× faster than unbounded)

```
Table 'FlowSteps'.  Scan count 12500, logical reads 62536    ← exactly 1/8th
Table 'FlowRuns'.   Scan count 1,     logical reads 2061
   CPU time = 995 ms,  elapsed time = 998 ms.        ← 7805 → 995 ms
max ActualRows across operators: 62500
```

Adding `fr.FlowId = @FlowId` narrows the outer run set to one of 8 flows
(`EstRows` for the join drops from 100,000 → 12,475), so the semi-join probes only
**12,500** step rows instead of 500,000 — `logical reads 500,497 → 62,536` and CPU
`7,805 → 995 ms`, an 8× reduction that tracks the flow fan-out exactly. The deep
cost is **linear in the number of step rows scanned**, so any predicate that shrinks
the outer run set shrinks deep latency proportionally. This is the **only lever that
works without schema/feature changes** and it is already reachable from the dashboard.

The **quick** tier never touches `FlowSteps` at all (STATISTICS IO shows no
`FlowSteps` line for quick unbounded — only `FlowRuns` logical reads 2061, CPU 338
ms), confirming the tiering does what it claims: quick is a pure run-table scan.

### SQL Server — ranked recommendations

1. **(Free, already wired, do this first) Bound the search.** `?flowId=` →
   **7.9 s → 1.0 s**; `?from=/?to=` narrows via `IX_FlowRuns_StartedAt`. A default
   date window on the runs list caps the worst case. This is the cheapest, safest,
   highest-leverage change and requires **no library change** — only a UI/default
   policy. Document deep+unbounded as "scans all step history; prefer a filter".

2. **(Targeted, moderate effort) Persisted computed search column on `FlowSteps`.**
   Add a persisted `SearchText` column = lower-cased concatenation of `StepKey`,
   `ErrorMessage`, and a **bounded prefix** of `OutputJson` (e.g. first 4 KB), then
   search `SearchText LIKE '%term%'`. Still non-sargable, but: (a) one narrow column
   instead of three NVARCHAR(MAX) reads per row → far fewer logical reads; (b)
   pre-lowercased removes the case-fold cost; (c) bounding the `OutputJson` prefix
   caps per-row CPU. Expect a large constant-factor reduction on the residual scan.
   Trade-off: write-time cost on every step completion + storage.

3. **(Best asymptotics, most effort) Separate searchable projection table.** A
   narrow `FlowStepSearch(RunId, StepKey, SearchText)` written alongside step
   completion, optionally with a tokenised inverted-index table for the common case
   of searching for an identifier (order id, correlation id) rather than arbitrary
   substrings. Lets the deep search hit a much smaller, search-tuned table. This is
   the only path to sub-100 ms deep search on SQL Server without FTS, but it is real
   schema + write-path work.

4. **(Last resort — flagged, with caveats) Full-Text Search.** `CREATE FULLTEXT
   INDEX` on `OutputJson` + `CONTAINS`/`FREETEXT` would make substring-ish search
   fast, but: (a) the team rejected FTS; (b) FTS is word/token-based, not true
   substring — `CONTAINS('"needle*"')` does prefix-of-token, not arbitrary
   infix, so it would change search semantics (a mid-token substring like the
   planted `needle-7f3a9c2e` inside a larger JSON value may not match the same way);
   (c) FTS has its own population lag and Availability-Group/edition considerations.
   Only revisit if 1–3 are insufficient and the semantic change is acceptable.

---

## Cross-backend summary

| | PostgreSQL | SQL Server |
|---|---|---|
| Deep bottleneck | `COALESCE(col,'')` wrapper makes the existing GIN trigram index **unusable**; correlated EXISTS de-correlates fine | `LIKE '%term%'` is **non-sargable** with no trigram/FTS → residual scan of all 500k step rows |
| Best fix | **Drop COALESCE + de-correlate to `IN`** → trigram engages (462→151 ms, step side 310→9 ms) | **Bound (flowId/date)** (7.9 s→1.0 s); then persisted search column / projection table |
| Free lever today | `?flowId=` 462→345, `?from/to=` (also helps quick) | `?flowId=` 7.9 s→1.0 s, `?from/to=` |
| Schema change needed | No (rewrite uses existing indexes) | Yes for sub-second unbounded (computed column / projection) |

The two backends fail for **different** reasons: PostgreSQL *has* the right index
but writes SQL that can't use it (a pure query-rewrite win); SQL Server lacks any
substring-capable index and the only no-schema lever is bounding the scan.

---

## Reproducing

```bash
# Throwaway harness (rebuild it from this doc's method if needed):
#   .investigation/SqlDeepSearchProbe — console app, references the SqlServer +
#   PostgreSQL store projects, spins Testcontainers, runs the real migrators,
#   seeds 100k runs x 5 steps via COPY / SqlBulkCopy, times the exact store SQL,
#   and captures EXPLAIN (ANALYZE, BUFFERS) (PG) + STATISTICS IO/XML (SQL Server).
cd .investigation/SqlDeepSearchProbe
dotnet run -c Release -- pg     100000 2000   # PostgreSQL
dotnet run -c Release -- mssql  100000 2000   # SQL Server
# args: [pg|mssql|both] [totalRuns] [needleEveryN]
```

The harness is intentionally **not** added to the solution / CI — it requires
Docker and takes minutes to seed. The store source under `src/` was not modified;
the recommended fixes above are proposals for follow-up (PostgreSQL #1 is a small,
behaviour-preserving query rewrite; the SQL Server fixes range from a free UI
default to schema work).
