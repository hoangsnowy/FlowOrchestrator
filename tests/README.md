# Tests

Tests are split into three categories, each with its own project layout, CI cadence, and rules. **Pick the category before you start writing** — putting a test in the wrong bucket is how the suite gets flaky again.

## Categories

| Category | Folder | Project suffix | CI cadence | Rules |
|---|---|---|---|---|
| **Unit** | `tests/unit/` | `*.UnitTests` | Every push & PR (must pass) | Pure NSubstitute mocks; no I/O; no `Task.Delay > 50ms`; no containers; no HTTP. Total wall-clock target: **< 60 s**. |
| **Integration** | `tests/integration/` | `*.IntegrationTests` | Every PR + push to main | Hits real DB via Testcontainers, `WebApplicationFactory`, Hangfire in-memory, or other real components. Docker required locally. |
| **Regression** | `tests/regression/` | `FlowOrchestrator.RegressionTests` | Nightly (03:00 UTC) + push to `main` + `workflow_dispatch` | Timing-sensitive (cron, polling, timeout) and concurrency stress (64-task gate contests). Has its own `xunit.runner.json` with parallelization disabled. |

## Picker

When you write a new test, pick the project this way:

1. **What component are you testing?** — that gives you the prefix (e.g. `Core`, `InMemory`, `Hangfire`, `Dashboard`, `SqlServer`, `PostgreSQL`, `Testing`).
2. **What kind of test?** — that gives you the suffix and folder:
   - Pure unit (mocks only) → `tests/unit/FlowOrchestrator.{Component}.UnitTests/`
   - Hits real infrastructure → `tests/integration/FlowOrchestrator.{Component}.IntegrationTests/`
   - Timing/concurrency/end-to-end stress → `tests/regression/FlowOrchestrator.RegressionTests/{Subfolder}/`

Examples:

| Test | Goes in |
|---|---|
| `FlowGraphPlanner` DAG evaluation, mocked store | `tests/unit/FlowOrchestrator.Core.UnitTests/Execution/` |
| Dapper query against a real SQL Server container | `tests/integration/FlowOrchestrator.SqlServer.IntegrationTests/` |
| Cron trigger fires after FastForwardAsync | `tests/regression/FlowOrchestrator.RegressionTests/Testing/` |
| 64-task concurrent dispatch claim race | `tests/regression/FlowOrchestrator.RegressionTests/Hosting/` or `/InMemory/` |

## Running locally

```bash
# Fast feedback while developing (≈ 30 s)
dotnet test FlowOrchestrator.UnitTests.slnf

# Before pushing anything that touches storage / dashboard / Hangfire wiring
dotnet test FlowOrchestrator.IntegrationTests.slnf

# Before merging anything that touches scheduling / polling / concurrency
dotnet test FlowOrchestrator.RegressionTests.slnf
```

## Anti-flakiness rules

These rules are **mandatory** — they are the reason the CI used to be flaky and the reason the test suite was reorganized.

1. **Never assert an upper bound on `Stopwatch.Elapsed`.** `Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2))` is the classic source of CI flake. Test correctness, not wall-clock speed.
2. **Never poll on a counter with a wall-clock deadline.** Patterns like `while (counter == 0) await Task.Delay(100)` are flaky on slow runners. Replace with a `TaskCompletionSource` that the handler/store/dispatcher resolves on the logical event you actually care about. Use `tcs.Task.WaitAsync(TimeSpan.FromSeconds(30))` to bound the wait.
3. **Be generous with timeouts.** When you must wait for real I/O or a real timer, default to ≥ 30 s. CI CPU contention is real.
4. **Concurrency stress goes in regression, not unit.** Anything with `Parallel.For`, `Task.WhenAll([...64 tasks])`, or `SemaphoreSlim`-style gate contests belongs in `tests/regression/` so it cannot block PRs while still catching regressions nightly.
5. **Real timers / real DB / real containers go in integration or regression, never unit.** Unit tests that use `Microsoft.AspNetCore.TestHost`, `Testcontainers.*`, or live `PeriodicTimer` are mis-categorized — move them.

## Layout

```
tests/
├── unit/
│   ├── FlowOrchestrator.Core.UnitTests/
│   ├── FlowOrchestrator.InMemory.UnitTests/
│   ├── FlowOrchestrator.Hangfire.UnitTests/
│   └── FlowOrchestrator.SqlServer.UnitTests/
├── integration/
│   ├── FlowOrchestrator.Dashboard.IntegrationTests/
│   ├── FlowOrchestrator.Hangfire.IntegrationTests/
│   ├── FlowOrchestrator.InMemory.IntegrationTests/
│   ├── FlowOrchestrator.PostgreSQL.IntegrationTests/
│   ├── FlowOrchestrator.SqlServer.IntegrationTests/
│   └── FlowOrchestrator.Testing.IntegrationTests/
└── regression/
    └── FlowOrchestrator.RegressionTests/
        ├── Execution/    # FlowGraphPlanner edge cases, PollableStepHandler boundaries
        ├── Hosting/      # Recovery concurrency stress
        ├── InMemory/     # Dispatcher concurrency, periodic timer trigger
        └── Testing/      # End-to-end cron, polling, timeout via FlowTestHost
```
