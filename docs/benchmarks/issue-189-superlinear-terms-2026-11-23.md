# #189 superlinear terms — classifier, planner, ledger, search — 2026-11-23

The 1.34.0 perf review ([#189, 2026-10-31 comment](https://github.com/hoangsnowy/FlowOrchestrator/issues/189))
listed four terms that grow with run length or run history beyond the per-run status cache (P0-1),
which stays out of scope here because it needs a design first. This document records before and after
for each one.

## Setup

BenchmarkDotNet 0.15.8, `[MemoryDiagnoser]`, `[SimpleJob(RuntimeMoniker.Net10_0)]` (the full default
job, not `--job short`), Release, .NET 10.0.12, Intel Core Ultra 7 255H, Windows 11. The "before"
column was measured on `origin/main` at `ecc1fee` (1.34.1) with the benchmarks added in this change.
The "after" column was measured on this branch. Loop shape everywhere:
`entry -> loop(child_0 -> child_1 -> child_2) -> after`, so a run with N iterations has about 3N status
rows.

```
cd tests/benchmarks/FlowOrchestrator.Benchmarks/bin/Release/net10.0
./FlowOrchestrator.Benchmarks.exe --filter "*RunTerminationClassifierBenchmarks*" "*FlowGraphPlannerLoopBenchmarks*" \
    "*LoopAdmissionDispatchLookupBenchmarks*" "*InMemoryRunSearchIdMatchBenchmarks*"
```

## 1. `RunTerminationClassifier` — O(n²) → O(n)

**Before.** `IsLeaf` scanned every status for every status. Each comparison called `DependsOn`, which
ran `FindStep` and built `$"{parent}.{dependency}"`. The cost was O(n²) in time and in allocation. It
ran once per run completion and once per recovered run.

**After.** One pass collects the non-leaf keys: each step's `RunAfter` dependencies resolved into
runtime space, plus every dotted ancestor of every runtime key. Each status is then a single set
lookup. A second pass does the same for failure handling, collecting the keys that succeeded steps
depend on. Keys are held as `StepKeySlice` views: a prefix of an existing string, optionally followed
by `"." + tail`. They hash and compare as the logical key, so no key string is built.

`benchmarks/RunTerminationClassifierBenchmarks.cs`:

| Case | Iterations | Before | After | Speed-up |
|---|---:|---:|---:|---:|
| all succeeded | 100 | 4,729 µs / 3.68 MB | 31.2 µs / 12.6 KB | 152× / 291× less memory |
| body skipped | 100 | 5,148 µs / 3.68 MB | 45.0 µs / 12.6 KB | 114× |
| all succeeded | 500 | 115,104 µs / 91.67 MB | 168 µs / 56.4 KB | 684× / 1,665× less memory |
| body skipped | 500 | 115,225 µs / 91.67 MB | 268 µs / 56.4 KB | 430× |

Going from 100 to 500 iterations now multiplies cost by about 5.4×, where it used to multiply by 24×.
The issue's own run of the old code measured 44.7 ms / 35 MB at 500. That harness had a smaller
manifest. The shape is the same.

**Equivalence.** `RunTerminationClassifierEquivalenceTests` keeps the old algorithm verbatim as an
oracle. It checks hand-written loop, nested-loop, recovery-handler and self-dependency cases, then a
seeded sweep of 3,000 random status maps, on net8.0, net9.0 and net10.0. Every case must classify
identically.

## 2. `FlowGraphPlanner.Evaluate` — no per-call `SortedSet` of every runtime key

**Before.** `BuildKnownStepKeys` made a `SortedSet` of every known key on every call: manifest keys,
every runtime key, and every expanded scope child. It split every runtime key, rebuilt every child
string and sorted the lot. `Evaluate` then skipped every key that already had a status. The engine
calls `Evaluate` two or more times per step completion.

**After.** Runtime keys are status keys by definition. So the keys `Evaluate` classifies are only
(manifest keys ∪ expanded children) minus the status map. `PlanCandidateStepKeys` builds that set in
one span-based walk per runtime key:

- It does not split anything and makes no `string.Join`.
- It does not call `FindStep` per prefix. The template resolution is carried incrementally.
- Each scope instance is deduplicated through a `StepKeySlice`, so the prefix string is never built.
- On .NET 9+, expanded children are tested against the status map with `GetAlternateLookup`. A child's
  string exists only if it is a candidate.

Only the candidates are sorted. The result is still O(n) per step completion in the status-map size,
the same order as reading the status map. That remaining term is P0-1.

`FlowGraphEvaluation.AllKnownStepKeys` is public, but no engine path reads it. It is now built lazily
from a snapshot of the status keys (one `string[]`), so its contents are identical to before even if
the caller mutates the map afterwards. A manifest with a dotted or empty top-level key keeps the
original eager path, because `FindStep`'s exact-match shortcut is reachable from a multi-segment
path. No real manifest has such a key. The same goes for runtime keys with empty or
whitespace-padded segments: they keep the original `Split`, so their normalisation is byte-identical.

`benchmarks/FlowGraphPlannerLoopBenchmarks.cs`, with the 500 case added. The loop is half complete:

| Iterations | Before | After | Speed-up |
|---:|---:|---:|---:|
| 10 | 3.38 µs / 7.1 KB | 1.44 µs / 1.08 KB | 2.3× / 6.6× less memory |
| 100 | 36.3 µs / 61.6 KB | 16.4 µs / 7.31 KB | 2.2× / 8.4× less memory |
| 500 | 237.5 µs / 303.7 KB | 81.2 µs / 34.2 KB | 2.9× / 8.9× less memory |

The 34 KB that remains at 500 iterations is the key snapshot (1,500 references, about 12 KB), the
scope-dedup set, and the candidate list.

**Equivalence.** `FlowGraphPlannerCandidateEquivalenceTests` keeps the old planner as an oracle. It
compares ready, blocked and waiting lists and `AllKnownStepKeys` element for element and in order. The
cases cover mid-run loops (`loop.3.child_2` blocked, `loop.3.recover` ready), nested loops
(`outer.0.inner.0.tail`), irregular keys (`loop. 4 .child_0`, `loop..5.child_0`), a dotted top-level
key, the snapshot contract, and a seeded sweep of 2,000 random status maps.

## 3. `ForEach` admission — ledger point lookups instead of the full set

**Before.** `AdmitLoopIterationsAsync` called `GetDispatchedStepKeysAsync` on every pass while a loop
was parked. That returns the run's whole dispatch ledger, which holds one row per dispatched step. On
SQL Server and PostgreSQL it is one row per step over the wire on every step completion.

**After.** The new `IFlowRunStore.IsStepDispatchedAsync(runId, stepKey)` follows the precedent of
`IsStepClaimedAsync`. Its default interface implementation delegates to the full read, so third-party
stores keep compiling. InMemory, SQL Server and PostgreSQL override it with a primary-key point
lookup.

`LoopAdmission.NextAdmissionsAsync` finds the started-iteration boundary in two phases:

1. A synchronous gallop over the status map alone. Its result is a proven lower bound on the true
   boundary.
2. A gallop forward from there on the full predicate. It consults the ledger only for indices without
   a status row.

In the sequential steady state this costs one ledger lookup per entry child. `NextAdmissions(…, set)`
stays as it was for recovery, which already holds the whole ledger.

`benchmarks/LoopAdmissionDispatchLookupBenchmarks.cs`, InMemory store, half the iterations settled:

| Measure | Iterations | Before (full set) | After (point lookup) |
|---|---:|---:|---:|
| ledger read alone | 100 | 5,042 ns / 4,808 B | 16.7 ns / 0 B |
| ledger read alone | 500 | 22,659 ns / 21,568 B | 18.0 ns / 0 B |
| whole admission decision | 100 | 13,760 ns / 23,408 B | 8,296 ns / 18,824 B |
| whole admission decision | 500 | 65,885 ns / 109,352 B | 39,777 ns / 88,008 B |

The ledger read is now flat in run length. The rest of the admission decision is the gate's backwards
active-count walk over settled iterations. That walk is O(settled iterations) and is documented on
`LoopAdmission.FindFirstPending` as needing settled state the engine does not track yet. It is not
part of this change. On the SQL backends, the win is the payload: one `SELECT 1 … WHERE RunId = @RunId
AND StepKey = @StepKey` replaces one row per dispatched step.

**Equivalence.** `LoopAdmissionPointLookupTests` covers:

- 3,000 random states that respect the prefix invariant, mixing ledger-only, in-flight and settled
  iterations, with limits 1–4 and 1–2 entry children. Each state must admit the same
  `loop.{i}.{child}` keys as the full-set gate.
- The exact lookup count in the sequential steady state.
- A dispatched-but-unstarted iteration, which is not re-admitted.
- A nested loop instance key (`outer.1.inner`).

Store behaviour is covered by `InMemoryFlowRunStoreIssue189Tests`,
`IsStepDispatchedDefaultImplementationTests`, and the SQL Server and PostgreSQL integration tests
`IsStepDispatchedAsync_*`.

## 4. `InMemoryFlowRunStore` search — no `run.Id.ToString()` per run

**Before.** Both search entry points, `GetRunsPageAsync` (`MatchesRunSearch`) and `SearchRunsAsync`
(`MatchesRunText`), formatted every candidate run's id into a new 36-character string to substring-match
it.

**After.** `IdContainsIgnoreCase` formats the id into a 36-character stack buffer and searches it as a
span (`OrdinalIgnoreCase`). A term longer than 36 characters returns immediately. The match result is
identical, including the empty term.

`benchmarks/InMemoryRunSearchIdMatchBenchmarks.cs`, a term that matches no run, step payloads excluded:

| Entry point | Runs | Before | After |
|---|---:|---:|---:|
| `GetRunsPageAsync(deepSearch:false)` | 1,000 | 49.1 µs / 102.2 KB | 36.0 µs / 8.4 KB |
| `SearchRunsAsync` | 1,000 | 53.0 µs / 102.0 KB | 36.4 µs / 8.2 KB |
| `GetRunsPageAsync(deepSearch:false)` | 10,000 | 457.4 µs / 1,016 KB | 341.4 µs / 78.8 KB |
| `SearchRunsAsync` | 10,000 | 515.6 µs / 1,016 KB | 378.8 µs / 78.6 KB |

The 8 bytes per run that remain come from `ConcurrentDictionary.Values`, which copies the values into
a list. That is out of scope for this change.

## Not addressed

- **P0-1, the per-run incremental status cache.** It is design-first and remains the dominant term:
  every step completion still reads the full status map, so `Evaluate` and the classifier stay O(n)
  per call.
- **`LoopAdmission`'s backwards active-count walk.** It is still O(settled iterations) per pass. See
  above.
