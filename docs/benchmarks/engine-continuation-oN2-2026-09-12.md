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

## 1. `LoopAdmission.NextAdmissions` — linear prefix walk

`src/FlowOrchestrator.Core/Execution/Internal/LoopAdmission.cs`

It found the first unstarted iteration by walking `0..k` from zero on every call,
and each probe built `$"{runtimeLoopKey}.{index}."` plus one `prefix + entry.Key`
concatenation per entry child, inside an `entries.Any(lambda)` closure.

`LoopAdmissionBenchmarks`, .NET 10, 3 children per iteration, `ConcurrencyLimit = 1`:

| Iterations | nothing started | half settled | all-but-last settled |
|---:|---:|---:|---:|
| 10 | 227 ns / 576 B | 1,389 ns / 3,296 B | 2,308 ns / 5,472 B |
| 100 | 192 ns / 576 B | 12,456 ns / 27,776 B | 25,768 ns / 54,432 B |
| 500 | 204 ns / 576 B | 68,397 ns / 136,576 B | **139,039 ns / 272,032 B** |

Perfectly linear in the number of already-started iterations — roughly 278 ns and
544 B per started iteration, per call. For a 500-iteration × 3-child loop that is
on the order of **100 ms of CPU and 200 MB of allocation per run** in the
admission gate alone.

**Change.** Started iterations form a prefix — `NextAdmissions` only ever admits
the contiguous block `[firstPending, firstPending + slots)`, so an iteration can
only have started if every lower one did. The boundary is now found by galloping
search (double the stride until an unstarted index appears) followed by a binary
search of the last interval: O(log n) probes. The closure and the per-child
concatenations are gone; the prefix is built once per probe.

The type's own `<remarks>` claimed the sequential default "costs one probe per
pass rather than a full re-walk". That was true of the backwards active-count
loop, not of this forward walk. Corrected in the same change.

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
| 25 | 433 ns / 832 B | 14.3 ns / **0 B** | 30× |
| 300 | 3,650 ns / 7,312 B | 13.9 ns / **0 B** | 262× |
| 1500 | 17,754 ns / 32,192 B | 13.5 ns / **0 B** | **1,315×** |

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

`StepInputResolutionBenchmarks`, `JsonElement` trigger payload (what every
first-party store actually hands the resolver):

| Inputs | shallow `@triggerBody().orderId` | 3-segment `.customer.address.city` |
|---:|---:|---:|
| 1 | 204 ns / 504 B | 305 ns / 688 B |
| 4 | 605 ns / 1,144 B | 934 ns / 1,880 B |
| 12 | 1,678 ns / 2,832 B | 2,734 ns / 5,040 B |

Linear in expression count: about 134 ns and 213 B per additional expression. A
12-input step paid 2.8 KB before its handler ran.

**This corrects the `expression-resolver-2026-05-02` baseline**, which attributes
the cost to `JsonSerializer.SerializeToElement`. That holds only for its POCO test
payload. With the production `JsonElement` payload the cost is almost entirely
string slicing: `Trim()`, two range-slices, then `Replace("[", ".")` +
`Replace("]", "")` + `Split('.')` — two intermediate strings, a `string[]`, and one
string per segment.

Sweeping payload size from 1 to 50 lines changes nothing (204 ns / 504 B versus
207 ns / 504 B), because `JsonElement.Clone()` returns `this` when the backing
document is non-disposable. **JSON size is not a factor**; the hypothesis that it
was is recorded here as ruled out.

**Change.** `TryResolvePath` walks the path as `ReadOnlySpan<char>`, treating `.`,
`[` and `]` as separators, and `TryGetPropertyRelaxed` takes a span so that
neither the exact-match attempt (`JsonElement.TryGetProperty(ReadOnlySpan<char>)`)
nor the case-insensitive sweep materialises the segment. Available on net8.0, so
no `#if` is involved.

## 5. Handler lookup

`DefaultStepExecutor` resolved the handler with
`_handlerMetadata.FirstOrDefault(h => string.Equals(h.Type, …))` on every step
execution — O(registered handlers) plus a closure allocation. The registry is
fixed for the executor's lifetime, so it is now indexed once into a
`FrozenDictionary<string, IStepHandlerMetadata>`. Duplicate registrations keep the
first entry, matching the previous `FirstOrDefault` semantics.

Not benchmarked separately; likely under 100 ns for typical handler counts.
Recorded for completeness rather than as a headline.

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

```bash
dotnet build FlowOrchestrator.slnx -c Release
cd tests/benchmarks/FlowOrchestrator.Benchmarks/bin/Release/net10.0
./FlowOrchestrator.Benchmarks.exe --filter "*LoopAdmission*"
./FlowOrchestrator.Benchmarks.exe --filter "*ContinuationCompletionGate*"
./FlowOrchestrator.Benchmarks.exe --filter "*StepInputResolution*"
```

The end-to-end ratios come from driving the Aspire AppHost's four sample
instances over HTTP and timing each `ForEach` iteration's signal-to-resume
interval — see `CLAUDE.md` §Performance Standards for why a trend, not an
absolute number, is the acceptance signal.
