# Engine continuation — unwinding O(n²) over a run — 2026-09-12

The DAG continuation runs on **every step completion**. Three things in it scaled
with the run's total step count, so a run of `n` steps did O(n²) work overall.
This document records what was measured, what changed, and what is still
outstanding.

Landed with the fix for [#188](https://github.com/hoangsnowy/FlowOrchestrator/issues/188).
The remaining items are tracked in
[#189](https://github.com/hoangsnowy/FlowOrchestrator/issues/189).

## Why it was invisible

The unit suite runs three-step flows. At that size every O(n) term in the
continuation is indistinguishable from O(1), and the suite was fully green with
all of this in place. It only appears once a `ForEach` runs to a realistic
iteration count, and even then it shows up as a *trend* rather than a failure:
every assertion still passes, each iteration just costs more than the last.

That is why the acceptance signal used below is the ratio between late and early
iterations, not an absolute number. An absolute threshold would have to be
loose enough to survive CI contention, and a loose threshold hides exactly this
class of regression.

## End-to-end: cost of a late iteration ÷ cost of an early one

Sample `WarehouseRobotFlow` (a `ForEach` whose body parks on `WaitForSignal`),
signalling each iteration in turn and timing it. First quartile average versus
last quartile average, same run.

| Iterations | Before | After |
|---:|---:|---:|
| 120 | 2.39× | — |
| 150 | 3.80× | 1.01×–1.61× depending on backend |

Per backend after the change, 80 iterations:

| Backend | Storage + runtime | Ratio |
|---|---|---:|
| `flow-sqlserver` | SQL Server + Hangfire | 1.01× |
| `flow-postgresql` | PostgreSQL + Hangfire | 1.39× |
| `flow-inmemory` | InMemory + InMemory | 1.61× |
| `flow-servicebus` | InMemory + Service Bus | 1.15× |

SQL Server looks flattest because its per-step database round-trips dominate and
submerge the engine-side term; InMemory is the most honest reading, because
nothing there masks it.

> [!IMPORTANT]
> **This is a ratio, not a throughput.** It deliberately divides out absolute cost so the *shape* of
> the scaling is visible, which means a backend can look excellent here while being slow in absolute
> terms. Service Bus is exactly that case: its 1.15× is the flattest-but-one reading on this table
> and its measured trigger throughput in the same session was **2.5 req/s against 62.8 for InMemory**
> — a 25× gap this table cannot show. Do not read the column as a runtime recommendation. The
> throughput finding is tracked in
> [#192](https://github.com/hoangsnowy/FlowOrchestrator/issues/192).

## 1. `LoopAdmission.NextAdmissions` — still O(n), and the change landed on the wrong loop

`src/FlowOrchestrator.Core/Execution/Internal/LoopAdmission.cs`

**This section corrects an earlier claim in this document.** The first version said the prefix walk
was the O(n) term and that replacing it with a galloping search made the method sublinear. Measuring
it properly says otherwise. The honest record matters more than the tidy story, so the original
numbers — which were taken from a separate analysis rather than measured here — are replaced by
these, all produced in one run on one machine.

`LoopAdmissionBenchmarks`, .NET 10, 3 children per iteration, `ConcurrencyLimit = 1`:

| Iterations | `NextAdmissions`, nothing started | half settled | all-but-last settled |
|---:|---:|---:|---:|
| 10 | 282 ns / 416 B | 1,350 ns / 2,616 B | 2,041 ns / 3,992 B |
| 100 | 283 ns / 416 B | 8,890 ns / 18,672 B | 17,236 ns / 35,624 B |
| 500 | 280 ns / 416 B | 42,880 ns / 87,856 B | **87,265 ns / 173,608 B** |

Cost grows linearly with the number of settled iterations — ×8.4 from 10 to 100, ×5.1 from 100 to
500. The method is still O(n) per call, so a loop run is still O(n²) overall.

### Why the prefix search was not the bottleneck

The forward walk that finds `firstPending` was genuinely O(n), and it is now a galloping + binary
search over the started-prefix invariant, which is O(log n) probes. But `NextAdmissions` contains a
second linear loop, and that one dominates:

```csharp
for (var index = firstPending - 1; index >= 0; index--)
{
    if (IsIterationSettled(scoped, runtimeLoopKey, index, statuses)) continue;
    if (++active >= limit) return [];
}
```

It counts how many already-admitted iterations are still in flight, and it can only stop early once
it has seen `ConcurrencyLimit` iterations that are **not** settled. In the common case — a sequential
loop where every earlier iteration has finished — it never stops early: it visits all of them,
building a prefix string and checking every entry child at each step.

The first version of this document also compared the two badly. It measured the prefix search alone
as "before" and the whole method as "after", which is not a comparison at all. Both columns above
measure `NextAdmissions` end to end at three sizes, which is the only shape that answers the
question.

**Status.** The galloping search stays: it is strictly cheaper than the walk it replaced, costs
nothing, and stops being masked the moment the backwards loop is addressed. Making that loop
sublinear needs settled-state the engine does not track today — deferred to
[#189](https://github.com/hoangsnowy/FlowOrchestrator/issues/189) rather than guessed at here.

The type's own `<remarks>` claimed the sequential default "costs one probe per pass rather than a
full re-walk". That is true of neither loop as written; corrected in the source.

## 2. Run-completion gate hashed the large side

`FlowOrchestratorEngine.Continuation.cs`, `FlowOrchestratorEngine.Control.cs` —
five call sites, all of this shape:

```csharp
if (claimed.Except(statuses.Keys, StringComparer.Ordinal).Any())
```

`Enumerable.Except` builds a hash set from its **second** argument before it
yields anything, so the cost was proportional to the run's total status-row count
regardless of how few keys were being tested. Compounded by the fact that claims
are never released on terminal statuses, so `claimed` also grows with the run.

`ContinuationCompletionGateBenchmarks`, two-element `claimed` list — a
conservative lower bound:

| Status rows | `Except(...).Any()` | `foreach` + `ContainsKey` | Speedup |
|---:|---:|---:|---:|
| 25 | 419.6 ns / 832 B | 14.4 ns / **0 B** | 29× |
| 300 | 3,608.9 ns / 7,312 B | 14.4 ns / **0 B** | 251× |
| 1500 | 17,917.9 ns / 32,192 B | 13.8 ns / **0 B** | **1,298×** |

The replacement is flat in the status-row count, as it must be — it never touches the large side —
while the original grows with it. That is the whole point: the gate is asked the same tiny question
on every step completion, and only one of the two shapes charges for the size of the run.

**Change.** Walk the small side: `foreach (var k in keys) if (!statuses.ContainsKey(k)) return true;`
Behaviour-identical — both store implementations build the status dictionary with
`StringComparer.Ordinal`, which is the comparer the old call site passed
explicitly — and allocation-free.

## 3. Unconditional status-map re-read

`FlowOrchestratorEngine.Continuation.cs` re-read the full status map immediately
after the blocked-step pass, on every step completion, whether or not that pass
had written anything. It usually writes nothing: most completions unblock no
dependents at all. Now conditional on the pass having actually recorded a skip.

Not separately benchmarked — the saving is one round-trip returning every step row
in the run, per completion, and is visible in the end-to-end ratios above.

## 4. Expression path resolution

`src/FlowOrchestrator.Core/Expressions/ExpressionPathHelper.cs`

`ExpressionPathWalkBenchmarks` isolates the path walk itself and measures both shapes in one run, so
the two columns are directly comparable:

| Path depth | BEFORE: `Replace` + `Split`, string-keyed | AFTER: span walk, span-keyed | Ratio |
|---:|---:|---:|---:|
| 1 | 34.0 ns / 32 B | 24.2 ns / **0 B** | 0.71× |
| 3 | 108.9 ns / 168 B | 65.9 ns / **0 B** | 0.60× |
| 6 | 202.3 ns / 312 B | 133.8 ns / **0 B** | 0.66× |

About a third of the time and **all** of the allocation removed, on a path that runs per expression,
per step input, per step.

**Change.** `TryResolvePath` walks the path as `ReadOnlySpan<char>`, treating `.`,
`[` and `]` as separators, and `TryGetPropertyRelaxed` takes a span so that
neither the exact-match attempt (`JsonElement.TryGetProperty(ReadOnlySpan<char>)`)
nor the case-insensitive sweep materialises the segment. Available on net8.0, so
no `#if` is involved.

### A correction carried over from the audit

The `expression-resolver-2026-05-02` baseline attributes this cost to
`JsonSerializer.SerializeToElement`. That holds only for its POCO test payload; with the production
`JsonElement` payload the cost is almost entirely string slicing, which is what the table above
measures directly.

Sweeping payload size from 1 to 50 lines was reported to change nothing, because
`JsonElement.Clone()` returns `this` when the backing document is non-disposable — so JSON size is
not a factor. That sweep comes from `StepInputResolutionBenchmarks`, which **was not re-run here**;
it is repeated as a pointer, not as a measurement of this change.

## 5. Handler lookup

`DefaultStepExecutor` resolved the handler with
`_handlerMetadata.FirstOrDefault(h => string.Equals(h.Type, …))` on every step
execution — O(registered handlers) plus a closure allocation. The registry is
fixed for the executor's lifetime, so it is now indexed once into a
`FrozenDictionary<string, IStepHandlerMetadata>`. Duplicate registrations keep the
first entry, matching the previous `FirstOrDefault` semantics.

`HandlerLookupBenchmarks`, looking up the handler registered last — not a pathological input, simply
whichever one the flow happens to use:

| Registered handlers | BEFORE: `FirstOrDefault` closure | AFTER: `FrozenDictionary` | Ratio |
|---:|---:|---:|---:|
| 8 | 74.9 ns | 14.6 ns | 0.20× |
| 32 | 224.8 ns | 13.4 ns | 0.06× |
| 128 | 317.6 ns | 14.8 ns | 0.05× |

The absolute numbers are small. What matters is the shape: the scan grows with the registry while the
lookup does not, so the gap widens in exactly the deployments that register the most handlers.

## 6. Storage-side reads

- **`GetRunDetailAsync` for a header field.** It issues three queries and returns
  every step and attempt row *including* their unbounded JSON columns. Under load
  on a 150-iteration `ForEach` it timed out on SQL Server (`Error Number:-2`,
  `SqlFlowRunStore.GetRunCoreAsync`), and the resulting connection-pool pressure
  made the dashboard unreachable. New `IFlowRunStore.GetRunAsync` returns the
  header row only; `FlowSignalDispatcher` and five dashboard endpoints use it.
- **Claim membership.** `GetClaimedStepKeysAsync` returns one key per executed step
  — claims are deliberately never released on terminal statuses — so asking
  "is this one step claimed?" through it was O(n) in payload and scan. New
  `IsStepClaimedAsync` is a point lookup on the composite primary key.
- **Dashboard "today" counts.** `CAST(CompletedAt AT TIME ZONE 'UTC' AS DATE) = …`
  is not sargable, so both counts scanned `FlowRuns` on an endpoint the dashboard
  polls every 5 s per connected browser. Replaced with a half-open range whose
  boundaries are computed in C#, preserving the deliberate UTC bucketing.
- **PostgreSQL covering index.** `ix_flow_steps_run_id_started_at` now carries the
  `INCLUDE (step_key, step_type, status, job_id, completed_at)` its SQL Server
  twin already had, so `GetStepStatusesAsync` can be an index-only scan instead of
  a heap fetch per row.

## Still outstanding

The dominant remaining term is `GetStepStatusesAsync` itself: it is O(n) by
construction and is called one to three times per step completion, which keeps a
long `ForEach` superlinear no matter what else is fixed. Addressing it needs a
per-run incremental status cache with a version or sequence to invalidate, and
that carries real correctness risk under multi-replica execution — two workers
must not disagree about a step's status. It is a design-first change, tracked in
[#189](https://github.com/hoangsnowy/FlowOrchestrator/issues/189) rather than
attempted here.

## Reproducing

Every number in sections 1, 2, 4 and 5 came from this one command, on an otherwise idle machine
(Windows 11, .NET 10, `--job short`), with no Aspire host, Docker container or load test running —
benchmarking a box that is simultaneously under load is how you end up publishing noise:

```bash
dotnet build tests/benchmarks/FlowOrchestrator.Benchmarks/FlowOrchestrator.Benchmarks.csproj -c Release
cd tests/benchmarks/FlowOrchestrator.Benchmarks/bin/Release/net10.0
./FlowOrchestrator.Benchmarks.exe \
  --filter "*LoopAdmissionBenchmarks*" "*ContinuationCompletionGate*" "*ExpressionPathWalk*" "*HandlerLookup*" \
  --job short
```

`ExpressionPathWalkBenchmarks` and `HandlerLookupBenchmarks` were added for this document, because
the existing cases only exercised the current implementation and so could only ever produce an
"after" column. Each carries a `[Benchmark(Baseline = true)]` that reproduces the shape being
replaced, so before and after are measured in the same run rather than stitched together across
sessions.

### What is not measured here

Six of the changes in this work touch storage and cannot be represented honestly by an in-process
microbenchmark: `GetRunAsync`, `IsStepClaimedAsync`, the sargable date predicates, the PostgreSQL
covering index, the retention leak, and the removed status-map re-read. Their evidence is
end-to-end — chiefly the disappearance of the SQL Server timeout described in section 6 — and they
are recorded as such rather than given invented numbers.

The end-to-end ratios at the top come from driving the Aspire AppHost's four sample
instances over HTTP and timing each `ForEach` iteration's signal-to-resume
interval — see `CLAUDE.md` §Performance Standards for why a trend, not an
absolute number, is the acceptance signal.
