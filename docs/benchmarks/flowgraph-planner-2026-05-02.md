# FlowGraphPlanner — Evaluate scaling benchmark, 2026-05-02

Measures `FlowGraphPlanner.Evaluate` on a linear flow without loops or
foreach — the common case where F2's manifest-key cache returns the
cached array directly without a SortedSet build, sort, or ToArray.

## Setup

- Linear flow: `step_0000` → `step_0001` → ... → `step_(N-1)` with each
  step's `RunAfter` referencing the previous one (Succeeded gate).
- Half the steps already completed (statuses populated for indices
  0..N/2). This matches what the engine's per-step-completion call
  pattern looks like mid-run.
- StepCount sweeps {5, 25, 100}.
- BenchmarkDotNet v0.15.8, .NET 10.0.6, Intel Core Ultra 7 255H, 16 cores.

## Results

| StepCount | BEFORE F2 | AFTER F2 | Speedup | Alloc BEFORE | Alloc AFTER | Alloc reduction |
|---:|---:|---:|---:|---:|---:|---:|
| 5 | 435 ns | 221 ns | 2.0× | 1.16 KB | 328 B | 3.6× |
| 25 | 2,001 ns | 778 ns | 2.6× | 3.85 KB | 568 B | 6.9× |
| 100 | 8,320 ns | 2,816 ns | 3.0× | 13.82 KB | 1,384 B | **10.2×** |

## Headline

F2 saves the per-call `SortedSet<string>` construction, the per-call
sort, and the `ToArray()` copy by caching the manifest's sorted key
list per `IFlowDefinition` reference (via `ConditionalWeakTable`). The
savings scale with manifest size:

- **2-3× faster Evaluate** across the whole sweep.
- **Allocation reduction grows with manifest size** — 3.6× at 5 steps,
  10.2× at 100 steps.

For typical 5-25 step flows the absolute saving is small (a few hundred
ns and a few KB per Evaluate call), but the engine calls Evaluate on
every step completion, so the savings compound under load. For larger
manifests the win is correspondingly larger.

## Reproducing

```bash
# from the repo root, with current HEAD
cd tests/benchmarks/FlowOrchestrator.Benchmarks/bin/Release/net10.0
./FlowOrchestrator.Benchmarks.exe --filter "*FlowGraphPlanner*"

# To recapture the BEFORE column:
git checkout 9dd2b45 -- src/FlowOrchestrator.Core/Execution/FlowGraphPlanner.cs
dotnet build -c Release
# rerun above
git checkout HEAD -- src/FlowOrchestrator.Core/Execution/FlowGraphPlanner.cs
```
