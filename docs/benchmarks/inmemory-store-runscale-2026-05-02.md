# InMemoryFlowRunStore — run-history scaling benchmark, 2026-05-02

Measures the per-call cost of the engine's hot-path reads on
`InMemoryFlowRunStore` as a function of total run history. The pre-F1
implementation scanned the global step keyspace
(O(total_steps_in_history)); F1 added per-run secondary indexes
(`_stepKeysByRun`, `_claimsByRun`, `_dispatchesByRun`) that reduce the
cost to O(steps_in_one_run). A flat curve as `TotalRuns` grows is the
proof of the asymptotic win.

## Setup

- Each run has 5 steps, all completed (Started → Dispatched → Claimed → Completed).
- TotalRuns sweeps {10, 100, 1000, 10000}.
- Target operation runs against the median run-id (the global keyspace
  is fully populated when the read fires).
- BenchmarkDotNet v0.15.8, .NET 10.0.6, Intel Core Ultra 7 255H, 16 cores.

## GetStepStatusesAsync

| TotalRuns | BEFORE F1 | AFTER F1 | Speedup | Alloc BEFORE | Alloc AFTER | Alloc reduction |
|---:|---:|---:|---:|---:|---:|---:|
| 10 | 1,298 ns | 692 ns | 1.9× | 2.42 KB | 592 B | 4.2× |
| 100 | 9,476 ns | 708 ns | 13× | 5.94 KB | 592 B | 10.3× |
| 1,000 | 47,959 ns | 691 ns | **70×** | 41.09 KB | 592 B | 71× |
| 10,000 | 1,532,553 ns | 744 ns | **2,059×** | 392.7 KB | 592 B | **663×** |

## GetClaimedStepKeysAsync

| TotalRuns | BEFORE F1 | AFTER F1 | Speedup |
|---:|---:|---:|---:|
| 10 | 1,462 ns | 266 ns | 5.5× |
| 100 | 11,921 ns | 271 ns | 44× |
| 1,000 | 129,515 ns | 268 ns | **483×** |
| 10,000 | 1,171,630 ns | 262 ns | **4,472×** |

## GetDispatchedStepKeysAsync

| TotalRuns | BEFORE F1 | AFTER F1 | Speedup |
|---:|---:|---:|---:|
| 10 | 1,643 ns | 341 ns | 4.8× |
| 100 | 9,254 ns | 333 ns | 27.8× |
| 1,000 | 125,349 ns | 329 ns | **381×** |
| 10,000 | 1,446,068 ns | 332 ns | **4,355×** |

## Headline

The pre-F1 implementation degraded **linearly** with total run history.
At 10,000 runs in store, a single `GetStepStatusesAsync` call took
**1.5 ms** and allocated **393 KB** — and the engine calls this method
twice per step completion. F1's secondary indexes flatten the curve
completely: the per-call cost stays at **~700 ns / 592 B** regardless
of how many other runs exist in store.

The 2,059× speedup at TotalRuns=10,000 is not a constant-factor
optimisation — it's an asymptotic complexity change from O(N) to O(s),
where N is global step history and s is steps in the queried run.
For long-lived processes with growing run history, this is a stability
fix, not just a perf win.

## Reproducing

```bash
# from the repo root, with current HEAD
cd tests/benchmarks/FlowOrchestrator.Benchmarks/bin/Release/net10.0
./FlowOrchestrator.Benchmarks.exe --filter "*InMemoryFlowRunStore*"

# To recapture the BEFORE column:
git checkout 2f13483 -- src/FlowOrchestrator.InMemory/InMemoryFlowRunStore.cs
dotnet build -c Release
# rerun above
git checkout HEAD -- src/FlowOrchestrator.InMemory/InMemoryFlowRunStore.cs
```
