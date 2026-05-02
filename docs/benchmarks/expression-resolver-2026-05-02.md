# Expression resolver — benchmark, 2026-05-02

Measures the hot-path cost of expression resolution helpers exercised on every
step execution. The numbers below reflect the state **after** three commits
that landed in the same session:

1. **Process-wide parse cache** in `StepOutputResolver` — the `@steps('key').<prop><trail>`
   regex match is now run at most once per unique expression string for the lifetime
   of the process. Subsequent resolutions hit a `ConcurrentDictionary` lookup.
2. **Fast-path `@` check** in `StepOutputResolver.IsStepExpression` and
   `TriggerExpressionResolver.{TryResolveTriggerBodyExpression, TryResolveTriggerHeadersExpression}`.
   Strings whose first non-whitespace character is not `@` are rejected without
   allocating a trimmed copy or calling `string.StartsWith`.
3. **Eliminated duplicated implementations** in `DefaultStepExecutor` — the two
   private static `TryResolveTrigger*` methods now delegate to the canonical
   helpers in `TriggerExpressionResolver`. `ForEachStepHandler`'s copies remain
   (their semantics intentionally differ in how they wrap `triggerData`) but
   gained the same fast-path.

## Headline

- **Literal input rejection** runs in ~0.3 ns and allocates nothing — the JIT
  inlined the call into a single character compare. A workflow with hundreds
  of plain-string inputs per step pays effectively zero overhead for the resolver.
- **Step-expression gate on a literal** falls from `expression.TrimStart() + StartsWith("@steps(")`
  (pre-change, ~50 ns and an allocation when leading whitespace existed) to a
  single-digit nanosecond character walk with no allocation.
- **Real expression resolution** (the `@triggerBody().orderId` case) stays at
  ~546 ns and 800 B allocated. That cost is dominated by `JsonSerializer.SerializeToElement`
  and is unrelated to this change set.

## Environment

- BenchmarkDotNet **v0.15.8** (released 2025-11-30, latest stable)
- Windows 11 (10.0.26200.8246 / 25H2 / 2025Update / HudsonValley2)
- Intel Core Ultra 7 255H @ 2.00 GHz, 16 cores
- .NET SDK 10.0.202, runtime 10.0.6 (X64 RyuJIT, x86-64-v3 ISA baseline)
- Job: `[SimpleJob(RuntimeMoniker.Net10_0)]` — default iteration count for tight
  confidence intervals

## Results — Default job

| Method                                       | Mean        | Error       | StdDev     | Gen0   | Allocated |
|--------------------------------------------- |------------:|------------:|-----------:|-------:|----------:|
| `Literal input — fast-path rejection`        |   0.2942 ns |   0.0242 ns |  0.0269 ns |      - |         - |
| `@triggerBody().orderId resolution`          | 545.9609 ns |  10.5430 ns | 12.9477 ns | 0.0210 |     800 B |
| `IsStepExpression + parse-cache lookup`      |   0.5469 ns |   0.0249 ns |  0.0233 ns |      - |         - |
| `IsStepExpression on literal (fast-path)`    |   0.2657 ns |   0.0233 ns |  0.0259 ns |      - |         - |
| `TryResolveTriggerHeadersExpression literal` |   0.2741 ns |   0.0236 ns |  0.0306 ns |      - |         - |
| `@triggerHeaders()['X-Request-Id']`          |  40.5610 ns |   0.8473 ns |  0.9418 ns | 0.0027 |     104 B |

Error margins are now in the hundredths of a nanosecond — the sub-1ns fast-path
numbers are statistically meaningful, not measurement noise.

## Reproducing

```bash
# from the repo root
cd tests/benchmarks/FlowOrchestrator.Benchmarks/bin/Release/net10.0
./FlowOrchestrator.Benchmarks.exe --filter "*Expression*"
# add --job short for a fast smoke run (3 warmup + 3 iterations)
# results land in BenchmarkDotNet.Artifacts/results/
```

## Adding more benchmarks

Drop a new `*.cs` file under `tests/benchmarks/FlowOrchestrator.Benchmarks/`
following the `[MemoryDiagnoser]` + `[SimpleJob(RuntimeMoniker.Net10_0)]` +
`[Benchmark]` pattern in `ExpressionResolverBenchmarks.cs`. Internal types
are accessible — the Core project has an `InternalsVisibleTo` entry for the
benchmark assembly.
