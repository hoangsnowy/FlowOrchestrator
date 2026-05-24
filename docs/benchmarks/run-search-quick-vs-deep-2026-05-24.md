# RUN search — quick vs deep tier cost, 2026-05-24

Quantifies the cost gap between the two RUN-search tiers added in v1.27.0
on `IFlowRunStore.GetRunsPageAsync(..., bool deepSearch, ...)`, measured
against the in-process `InMemoryFlowRunStore`.

- **Quick** (`deepSearch: false`): matches the search term only against run
  identity columns (id, flow name, trigger key, status, background job id),
  then short-circuits. Work is O(runs).
- **Deep** (`deepSearch: true`): additionally scans the step rows for every
  run that survives the identity filter.

Benchmark: `tests/benchmarks/FlowOrchestrator.Benchmarks/RunSearchBenchmarks.cs`.

## Why the deep path is expensive in the in-memory store

`InMemoryFlowRunStore.MatchesRunSearch`
(`src/FlowOrchestrator.InMemory/InMemoryFlowRunStore.cs:678`) implements the
deep branch as:

```csharp
return _steps.Values.Any(s =>
    s.RunId == run.Id
    && (ContainsIgnoreCase(s.StepKey, search)
        || ContainsIgnoreCase(s.ErrorMessage, search)
        || ContainsIgnoreCase(s.OutputJson, search)));
```

`_steps` is the **global** step dictionary across all runs in history. The
predicate filters by `s.RunId == run.Id` *inside* the scan, so each candidate
run walks the entire global step keyspace — O(total_steps). That predicate runs
once per run surviving the identity filter (`ApplyRunsFilter` →
`MatchesRunSearch`, line 672-673), so the whole search is
**O(runs × total_steps)**. With a fixed steps-per-run, total_steps grows with
run count, making the deep path scale **quadratically** with history size.

The quick path returns `false` immediately after the identity-column checks
(`InMemoryFlowRunStore.cs:689-690`), so it never touches `_steps`.

## Setup

- Each run has 6 completed steps (Started → Dispatched → Claimed → Completed).
- Every step's `OutputJson` is a ~300-byte JSON blob; the search needle
  (`needle-7f3a`) is planted in exactly **one** step's output per run and in
  **no** identity column. So the quick path is a true negative (0 matches, no
  step scan) and the deep path does the full representative scan and matches.
- `take: 20` (the dashboard default page size).
- `TotalRuns` sweeps {1,000, 10,000}; total steps in store = 6 × TotalRuns.
- In-process emit toolchain, ShortRun (3 warmup / 3 iterations / 1 launch) —
  the deep/10,000 cell is ~25 s per op, so a full job is infeasible.
- BenchmarkDotNet v0.15.8, .NET 10.0.6, Intel Core Ultra 7 255H, 16 cores.

## Results — before fix (original quadratic deep branch)

Figures below are from a flag-free reproduction run; a prior `--job short` run
agreed within run-to-run noise (e.g. quick/1,000: 61.15 µs vs 62.12 µs; deep
allocations identical to the byte). Allocations are deterministic and the
strongest signal; the deep/10,000 time has wide variance (n=3 short iterations
over a ~25 s op) but the order of magnitude and the quadratic shape are
unambiguous.

| TotalRuns | Quick mean | Quick alloc | Deep mean | Deep alloc | Deep ÷ Quick (time) | Deep ÷ Quick (alloc) |
|---:|---:|---:|---:|---:|---:|---:|
| 1,000  | 62.12 µs   | 133.44 KB   | 91.09 ms  | 47,185 KB (≈46 MB)     | **1,466×** | **354×** |
| 10,000 | 1,060.7 µs | 1,328.75 KB | 24,966 ms (≈25.0 s) | 4,691,243 KB (≈4.6 GB) | **23,537×** | **3,530×** |

## Fix — per-run step-key index

The deep branch now enumerates the run's own steps via the existing
`_stepKeysByRun` secondary index and direct-looks-up each in `_steps`, instead
of scanning the global `_steps` dictionary
(`src/FlowOrchestrator.InMemory/InMemoryFlowRunStore.cs`, `MatchesRunSearch`):

```csharp
if (!_stepKeysByRun.TryGetValue(run.Id, out var stepKeys))
    return false;
foreach (var stepKey in stepKeys.Keys)
{
    if (_steps.TryGetValue((run.Id, stepKey), out var s)
        && (ContainsIgnoreCase(s.StepKey, search)
            || ContainsIgnoreCase(s.ErrorMessage, search)
            || ContainsIgnoreCase(s.OutputJson, search)))
    {
        return true;
    }
}
return false;
```

This makes the per-run cost O(steps_in_run) instead of O(total_steps), so the
whole deep search is O(runs × steps_in_run) — linear in history size, same
asymptotic shape as the quick tier (deep just does a small constant of extra
work per run). It mirrors the index `GetRunDetailAsync` already uses.

## Results — after fix (O(runs × steps_in_run))

| TotalRuns | Quick mean | Quick alloc | Deep mean | Deep alloc | Deep ÷ Quick (time) | Deep ÷ Quick (alloc) |
|---:|---:|---:|---:|---:|---:|---:|
| 1,000  | 56.44 µs   | 102.19 KB   | 1.46 ms   | 262.83 KB   | ~26×  | ~2.6× |
| 10,000 | 1,532.5 µs | 1,016.25 KB | 24.17 ms  | 2,622.36 KB | ~16×  | ~2.6× |

Deep 1,000 → 10,000 now scales ~16× in time (was 274×) — linear, not quadratic.
At 10,000 runs the deep search dropped from **~24,966 ms → ~24 ms (~1,040×)** and
**~4.6 GB → ~2.6 MB allocations (~1,800×)**. (Deep times at n=3 short iterations
have wide CI; the order of magnitude and the now-linear scaling are the signal.)

## Headline

The two tiers are not a constant-factor difference — they are different
complexity classes. Scaling `TotalRuns` from 1,000 to 10,000 (10×):

- **Quick** grows ~17× in time (1,061 µs / 62 µs) — linear-ish, dominated by
  LINQ `Where`/`OrderByDescending`/`ToList` over the run set, and ~10× in
  allocation (133 KB → 1.33 MB), matching the run-count scaling.
- **Deep** grows ~274× in time (24,966 ms / 91 ms) and ~99× in allocation
  (46 MB → 4.6 GB) — quadratic, exactly the O(runs × total_steps) blow-up
  predicted above (10× runs × 10× total_steps ≈ 100×).

At 10,000 runs in store, a single deep search takes **~25 seconds** and
allocates **4.6 GB** (driving sustained Gen2 collections), versus **~1 ms /
1.3 MB** for quick. Choosing the quick tier when the caller does not need
step-body matching is a **~23,500× latency win** and a **~3,500× allocation
reduction** at that scale.

This is the data backing the v1.27.0 design decision to default the dashboard
run list to the quick tier and gate deep search behind an explicit opt-in.

The numbers above are the **original** quadratic implementation; the index fix
documented in "Fix — per-run step-key index" collapses the in-memory deep path
to linear (24,966 ms → ~24 ms at 10,000 runs). Quick remains the right default
for typeahead, but deep is no longer a multi-second, multi-GB cliff on a store
with history.

> Note: the in-memory store is the most extreme case because the deep predicate
> rescans the *global* `_steps` per candidate run. The SQL Server / PostgreSQL
> stores push the step match into a single SQL statement (EXISTS / join), so
> their deep-vs-quick ratio is smaller — but the asymptotic shape (deep adds a
> per-run step scan the quick path skips) is the same. This benchmark
> characterises the in-memory runtime; a Testcontainers-backed SQL benchmark is
> intentionally out of scope to keep the suite dependency-free and CI-runnable.

## Reproducing

```bash
# from the repo root, with current HEAD
cd tests/benchmarks/FlowOrchestrator.Benchmarks/bin/Release/net10.0
./FlowOrchestrator.Benchmarks.exe --filter "*RunSearchBenchmarks*"
```

No `--job` / `--inProcess` flags are needed — the benchmark pins the in-process
emit toolchain and a ShortRun job via `[Config(typeof(RunSearchConfig))]`. The
in-process toolchain is required because the repo's `.claude/worktrees/` copies
of this project otherwise make BenchmarkDotNet's default toolchain fail on a
duplicate-project-name discovery error.
