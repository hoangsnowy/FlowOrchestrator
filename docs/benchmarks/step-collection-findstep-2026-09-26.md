# StepCollection.FindStep — runtime loop keys without allocation — 2026-09-26

`StepCollection.FindStep` resolves a runtime step key such as `"process.37.validate"` to the
template step in the manifest. `LoopBarrier.RunningLoopKeys` calls it once per status-map entry on
every step completion, and the continuation calls it once per ready step. So it sits directly on the
path that [#189](https://github.com/hoangsnowy/FlowOrchestrator/issues/189) tracks as O(n) per
completion (P0 item 2).

## What changed

Before, any key that missed the top-level dictionary went through
`key.Split('.', RemoveEmptyEntries | TrimEntries)`. That allocated a `string[]` plus one string per
segment, and then recursed.

Now the key is walked as trimmed, non-empty `ReadOnlySpan<char>` segments. On .NET 9 and later each
level is probed with `Dictionary.GetAlternateLookup<ReadOnlySpan<char>>()`, so no segment string is
ever created. .NET 8 still materialises each segment for the dictionary probe, but no longer allocates
the array or a string for the numeric iteration segments.

Semantics are unchanged: segments are trimmed, empty ones are ignored, numeric segments are skipped
only after a scoped step, and keys with fewer than two segments return `null`.
`StepCollectionFindStepEquivalenceTests` pins this by running the old implementation as an oracle
against 35 key shapes.

## Numbers

`tests/benchmarks/FlowOrchestrator.Benchmarks/StepCollectionFindStepBenchmarks.cs`,
`[MemoryDiagnoser]`, `[SimpleJob(RuntimeMoniker.Net10_0)]`, Release, .NET SDK 10.0.401. The
baseline row is the pre-#189 implementation, kept verbatim in the benchmark.

| Key | Legacy `Split` | Span walk (shipped) | Ratio | Allocated (legacy → shipped) |
|---|---:|---:|---:|---:|
| `finalize` (top-level hit) | 3.09 ns | 3.09 ns | 1.00 | 0 B → 0 B |
| `process.37.validate` (one loop level) | 78.9 ns | 47.1 ns | **0.60** | 160 B → **0 B** |
| `process.37.inner.4.leaf` (two loop levels) | 120.3 ns | 73.6 ns | **0.61** | 224 B → **0 B** |

The top-level fast path is unchanged. Runtime loop keys are about 40% faster and no longer allocate.
On a 500-iteration `ForEach`, that removes 160–224 B of garbage per status row, per step completion.

## Not covered here

The dominant remaining term in #189 is `GetStepStatusesAsync`: one round-trip returning every step
row, one to three times per completion. That needs the per-run incremental status cache described in
the issue. It is a design-first change and is not part of this PR.
