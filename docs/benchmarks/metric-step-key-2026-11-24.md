# Bounded `step_key` metric tag — 2026-11-24

Every step metric (`flow_step_duration_ms`, queue delay, claim lost, poll attempts, retries, skips,
signal wait) was tagged with the **runtime** step key. ForEach children are keyed
`{loop}.{index}.{child}`, so each iteration index became a new time series — cardinality proportional
to the collection size, which OpenTelemetry SDKs cap (2,000 series per instrument by default) before
folding points into an overflow series.

The tag is now the manifest (template) key: `MetricStepKey.For` removes the numeric iteration
segments (`process.37.validate` → `process.validate`). Traces and logs keep the full runtime key.

## Cost

`MetricStepKey.For` runs for each step metric, i.e. a few times per step completion. A key with no
numeric segment is returned as is; a loop key's template is composed on the stack and resolved through
a `ConcurrentDictionary` span alternate lookup, so its string is allocated once per distinct template
(capped at 4,096 cached templates), not per call. Per call: O(key length), independent of the run's
step count.

## Numbers

`tests/benchmarks/FlowOrchestrator.Benchmarks/MetricStepKeyBenchmarks.cs`, `[MemoryDiagnoser]`,
Release, run with `--inProcess` on .NET 11 (the benchmark project targets net11.0 only, so the
pinned `Net10_0` job cannot build out of process). Cache warmed in `[GlobalSetup]`, as after the
first iteration of a loop.

| Key                       | Mean      | Allocated |
| ------------------------- | --------: | --------: |
| `finalize`                |  2.546 ns |         - |
| `process.37.validate`     | 57.593 ns |         - |
| `process.37.inner.4.leaf` | 66.434 ns |         - |

Before the change the tag cost nothing to compute; the price of a bounded series count is ~60 ns and
zero bytes per loop-step metric.
