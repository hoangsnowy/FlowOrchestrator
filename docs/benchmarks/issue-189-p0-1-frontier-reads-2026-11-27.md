# #189 P0-1 — frontier reads instead of the whole-run status map — 2026-11-27

The engine runs its continuation on every step completion. Before this change that continuation read
**every status row of the run** one to three times (`GetStepStatusesAsync(runId)`), so a `ForEach` of n
iterations read O(n²) rows over its life. The stress harness measured it on SQL Server + Hangfire
(`prepare → ForEach{ c0 → c1 } → done`): 413 status rows per iteration at 100 items, 4 013 at 1 000,
total rows scaling as items^1.99.

The continuation now reads only what a completion can have changed (RFC 2.0 §3.5): the dependents of the
changed step in its scope instance and their dependencies, by key. The completion gate asks an indexed
existence query first and reads the whole run only once nothing is open, i.e. when the run is about to
close. The ForEach barrier and admission read the loop's settled-iteration count from the store (one row)
instead of scanning the status map.

## Setup

- Shape: `prepare → ForEach(items){ c0 → c1 } → done` — the stress harness's `foreach-scaling` flow —
  run through the real `FlowOrchestratorEngine` on the in-memory store with a FIFO in-process dispatcher
  (`tests/benchmarks/FlowOrchestrator.Benchmarks/Support/ForEachRunDriver.cs`). The step executor is a
  stub, so the numbers are the engine's own per-completion work.
- Store traffic is counted by a `DispatchProxy` around the run and runtime stores, so the same harness
  measured both columns without changes to the counting code.
- BenchmarkDotNet 0.15.8, `[MemoryDiagnoser]`, one pinned in-process job (1 launch, 2 warm-up, 8 measured
  iterations), Release, .NET 11.0.0, Intel Core Ultra 7 255H, Windows 11. In-process because
  BenchmarkDotNet 0.15.8 cannot build or validate a net11.0 job out of process (it has no `Net11_0`
  moniker; a `Net10_0` job fails NU1201 against this net11.0-only project).
- **Before** = `247af7f` (the benchmark commit, engine unchanged from `c28b017`). **After** = `093c2a0`.

```
dotnet run -c Release --project tests/benchmarks/FlowOrchestrator.Benchmarks -- --store-reads
dotnet run -c Release --project tests/benchmarks/FlowOrchestrator.Benchmarks -- --filter "*ForEachContinuationBenchmarks*"
```

## 1. Rows read per step completion — the pass condition

Deterministic counts, one run per row. "Status rows" are the rows every `GetStepStatusesAsync` overload
returned, divided by the run's step executions; the closing full read of the run is included (it adds
≈ 1.0 per completion after the change, once per run). "Claim + ledger rows" are the full-set
`GetClaimedStepKeysAsync` / `GetDispatchedStepKeysAsync` reads, which only the closing gate makes.

| Items | Limit | Store calls / completion (before → after) | Status rows / completion (before → after) | Claim + ledger rows / completion |
|---:|---:|---:|---:|---:|
| 100 | 1 | 8.0 → 8.0 | 203.5 → **4.0** | 2.0 → 2.0 |
| 250 | 1 | 8.0 → 8.0 | 503.5 → **4.0** | 2.0 → 2.0 |
| 500 | 1 | 8.0 → 8.0 | 1 003.5 → **4.0** | 2.0 → 2.0 |
| 1 000 | 1 | 8.0 → 8.0 | 2 003.5 → **4.0** | 2.0 → 2.0 |
| 100 | 16 | 19.7 → 11.0 | 196.5 → **7.2** | 2.0 → 2.0 |
| 250 | 16 | 20.3 → 11.4 | 496.2 → **7.6** | 2.0 → 2.0 |
| 500 | 16 | 20.5 → 11.6 | 996.1 → **7.6** | 2.0 → 2.0 |
| 1 000 | 16 | 20.6 → 11.7 | 1 996.1 → **7.7** | 2.0 → 2.0 |

Per completion the rows read are flat in n (×1.0 from 100 to 1 000 items, against ×9.8 before). Per
iteration that is 8 rows at 1 000 items where the harness measured 4 013.

## 2. Whole run, in-memory store

| Items | Limit | Before | After | Before alloc | After alloc |
|---:|---:|---:|---:|---:|---:|
| 100 | 1 | 14.68 ms | 1.87 ms | 8.29 MB | 1.87 MB |
| 100 | 16 | 10.83 ms | 1.77 ms | 9.80 MB | 2.30 MB |
| 1 000 | 1 | 850.14 ms | 61.62 ms | 647.04 MB | 17.58 MB |
| 1 000 | 16 | 894.33 ms | 69.81 ms | 664.05 MB | 23.48 MB |

Allocation is now linear (×9.4 for ×10 items; it was ×78). **Time is not yet linear** (×33 for ×10 items;
it was ×58). The remaining growth is the settled-iteration count, below.

## 3. What still grows: the settled-iteration count

`IFlowRunRuntimeStore.CountSettledIterationsAsync` answers "how many iterations of this loop have every
body row terminal?" in one row, so it is flat on the wire. Server-side it is a range read over the loop's
own rows, grouped by iteration — O(rows of the loop) — and the engine asks it once per **iteration
settle** (not per completion), and in the tail of a loop only when the last iteration has settled.

Per-call cost, measured against Testcontainers images on the same machine (40 calls after 5 warm-ups;
run of 2n + 1 rows, every row with 300-character input and output JSON):

| Backend | Loop rows | Full status read | Keyed read (5 keys) | Open-row check | Settled count |
|---|---:|---:|---:|---:|---:|
| SQL Server 2022 | 2 000 | 1.4–3.4 ms | 0.55 ms | 0.65 ms | 3.3 ms |
| SQL Server 2022 | 10 000 | 6.3–8.0 ms | 1.2 ms | 1.2 ms | 16.4 ms |
| PostgreSQL 18 | 2 000 | 1.8–3.2 ms | 0.86 ms | 0.56 ms | 4.8–6.0 ms |
| PostgreSQL 18 | 10 000 | 4.3–4.7 ms | 0.55 ms | 0.48 ms | 18–21 ms |

All times are client-observed round trips, so the full-read column includes transferring and
materialising every row. The former engine made about four full status reads per iteration of this shape
(table 1: 4 004 status reads for 1 000 iterations), and then built a dictionary and ran a full planner
evaluation over each. The new one makes one settled count plus about five keyed or point reads per
iteration. Per iteration that is comparable at 2 000 loop rows on both backends (SQL Server ≈ 6 ms
against 6–14 ms; PostgreSQL ≈ 8–10 ms against 7–13 ms), about 25 % less at 10 000 rows on SQL Server
(≈ 22 ms against 25–32 ms), and about 20 % **more** at 10 000 rows on PostgreSQL (≈ 21–24 ms against
17–19 ms) — while the rows sent to and materialised by the engine drop by ~500×. The count is linear per
iteration, so a loop is still quadratic in database CPU, with a constant no better than before on
PostgreSQL. The first form of the SQL Server query
cost 9.5 ms / 55 ms (re-parsing every row under the database collation); grouping on the raw segment
and checking the index once per group brought it to the numbers above. PostgreSQL's text functions cost
about four times a plain index scan per row; a bare `COUNT(*)` over the same range is 1.5 ms / 3.8 ms.

**Not fixed here, deliberately.** Making the loop barrier O(1) needs per-loop counters that are updated
in the same transaction as the status write that settles an iteration (RFC 2.0 §4.7: the loop row with
`OpenIterations` / `AdmittedIterations` under a row lock). Any counter kept outside that transaction loses
increments on a crash and then under-admits or never settles a loop — strictly worse than the derived
count, which every later settle recomputes from the rows themselves. The settled count keeps the barrier
exact, idempotent and crash-tolerant across replicas, which is the hard constraint; the counters belong
with the `CompleteStepAsync` / `FlowStepWork` redesign.

## 4. Not measured here

- The stress harness (`foreach-scaling`, `throughput`) against SQL Server + Hangfire and PostgreSQL. It
  is the measurement CLAUDE.md requires for an engine-continuation change; run it on the branch head
  before merging and put the numbers in the PR. Expected: `status rows / iter` flat at ≈ 8 instead of
  4 013 at 1 000 items, wall time dominated by the runtime's dispatch cadence as before.
