# Contributing to FlowOrchestrator

Thanks for your interest in making FlowOrchestrator better. This guide covers how to set up a local environment, the conventions we follow, and how to get a PR merged.

If you only want to **ask a question**, please use [GitHub Discussions](https://github.com/hoangsnowy/FlowOrchestrator/discussions) instead of opening an issue.

If you found a **security vulnerability**, do not open a public issue — see [`SECURITY.md`](SECURITY.md) for the private disclosure process.

---

## Quick start

```bash
# Clone & build
git clone https://github.com/hoangsnowy/FlowOrchestrator.git
cd FlowOrchestrator
dotnet build

# Run the fast unit-test suite (~30 s, no Docker needed)
dotnet test FlowOrchestrator.UnitTests.slnx

# Run the sample app (requires Docker — Aspire spins up SQL Server)
dotnet run --project ./FlowOrchestrator.AppHost/FlowOrchestrator.AppHost.csproj
```

Libraries target `net8.0`, `net9.0`, and `net10.0`. The sample app and AppHost target the latest SDK.

---

## Project layout

The high-level architecture is documented in [`CLAUDE.md`](CLAUDE.md) — please skim the **Architecture** section before your first PR.

```
src/
  FlowOrchestrator.Core         # runtime-agnostic engine + abstractions
  FlowOrchestrator.Hangfire     # Hangfire runtime adapter
  FlowOrchestrator.InMemory     # in-process runtime adapter (no infra)
  FlowOrchestrator.ServiceBus   # Azure Service Bus runtime adapter (multi-replica)
  FlowOrchestrator.SqlServer    # Dapper-based SQL Server persistence
  FlowOrchestrator.PostgreSQL   # Dapper-based PostgreSQL persistence
  FlowOrchestrator.Dashboard    # built-in /flows HTML+JS dashboard
  FlowOrchestrator.Testing      # published test-host package (FlowTestHost) for consumers
samples/                        # working sample apps
FlowOrchestrator.AppHost/       # .NET Aspire host for local development
tests/
  unit/                         # fast, no I/O — runs on every PR
  integration/                  # Testcontainers + WebApplicationFactory
  regression/                   # timing-sensitive + concurrency stress
docs/                           # DocFX site published to GitHub Pages
```

---

## Coding standards

### `.editorconfig` + `dotnet format`

The repo ships an [`.editorconfig`](.editorconfig) that codifies indentation, brace style, `using` ordering, and naming conventions.

`Directory.Build.props` sets `EnforceCodeStyleInBuild=true` **and** `TreatWarningsAsErrors=true`, so any `.editorconfig` rule raised at warning severity or above fails `dotnet build` — locally and in CI. There is no separate `dotnet format` job; a clean build *is* the style gate.

```bash
dotnet build                        # the real gate — must be 0 errors, 0 warnings
dotnet format                       # auto-fix before you get there
dotnet format --verify-no-changes   # optional: report without writing
```

### XML doc comments are mandatory

Every new `.cs` file must have `///` XML doc on every `public` and `protected` type and member. The full convention (required tags, what to include in `<remarks>`, examples) lives in [`CLAUDE.md`](CLAUDE.md) under **Documentation Standards**.

### Tests

- Framework: **xUnit + NSubstitute** with plain `Assert.*`. **Do not** add FluentAssertions, Shouldly, or any fluent-assertion library.
- Every `[Fact]` / `[Theory]` body must contain `// Arrange`, `// Act`, `// Assert` comment blocks (AAA pattern).
- See [`tests/README.md`](tests/README.md) for which test category your test belongs in (unit vs integration vs regression).

### Anti-flakiness rules

- Never assert an upper bound on `Stopwatch.Elapsed`.
- Never poll a counter with a wall-clock deadline. Wait on a `TaskCompletionSource` set by the handler.
- For genuine timeout assertions, pick a generous budget (≥ 30 s) so CI CPU contention doesn't trip it.

### Integration-test flake taxonomy

Two patterns have caused red CI runs that turned green on retry. Treat any new test that triggers either as a bug — fix it, do not just rerun.

1. **Time-boundary flakes** — assertions that depend on `DateTimeOffset.UtcNow` falling inside a specific bucket (hour, minute, day) fail at exactly `XX:00` when bucketing crosses a boundary mid-test. Snap input timestamps to a known boundary (truncate to hour / floor / inject a `TimeProvider`) instead of relying on wall-clock alignment.
2. **NSubstitute `Received(N)` race on async/fire-and-forget paths** — asserting `mock.Received().CompleteRunAsync(...)` immediately after dispatching async work. The call may not have landed yet. Wait on a logical event (`TaskCompletionSource` set inside `When().Do(_ => tcs.TrySetResult())`) before asserting received-call counts.

### HTTP endpoints

If your change adds an HTTP endpoint that returns >1 KB typical payload, the **HTTP endpoints** checklist in [`CLAUDE.md`](CLAUDE.md) applies:

- Honor `Accept-Encoding` (Brotli/Gzip/raw).
- Emit `Vary: Accept-Encoding`.
- Add a test that decompresses the encoded response and asserts byte equality.
- Use `CompressionLevel.Optimal` for pre-compressed static pages, `CompressionLevel.Fastest` for per-request dynamic responses.

### Dashboard endpoints — DI pitfall

The Dashboard integration tests bootstrap a minimal server that **bypasses `AddFlowOrchestrator`**. Adding a new constructor / minimal-API parameter pulled from DI silently 400s the request in those tests with no clear error.

When adding or changing a Dashboard endpoint signature: resolve optional services from `HttpContext.RequestServices` with a default, e.g.

```csharp
var opts = http.RequestServices.GetService<FlowRunControlOptions>() ?? new();
```

instead of taking `FlowRunControlOptions opts` directly as a handler parameter. Verify locally with `dotnet test tests/integration/FlowOrchestrator.Dashboard.IntegrationTests/...` (no Docker, ~3 s) before pushing.

### Dashboard UI

The dashboard's CSS, HTML, and JS live as embedded resources under `src/FlowOrchestrator.Dashboard/Assets/` — `dashboard.css`, `index.html`, and `dashboard.js`. (`DashboardHtml.cs` is only the loader: it stitches the assets together and pre-compresses the result to Brotli + Gzip once at startup.)

All changes to those assets must follow [`DESIGN.md`](DESIGN.md). The repo uses a warm-toned palette — no cool blue-grays, no gradients. Every colour, radius, and font value must be a `--fo-*` / `--r-*` token declared in `dashboard.css :root`; a new token also has to be listed in `DESIGN.md §10`.

---

## Adding a custom storage backend or runtime

FlowOrchestrator's storage and runtime are pluggable by design.

- **Storage backend** — implement `IFlowStore`, `IFlowRunStore`, `IFlowRunRuntimeStore`, `IFlowRunControlStore`, `IOutputsRepository`, `IFlowEventReader`, `IFlowSignalStore`, and `IFlowRetentionStore`. `IFlowScheduleStateStore` is optional — `AddFlowOrchestrator` falls back to a non-persistent in-process store when it is unregistered. Note that `IFlowSignalStore` has **no** fallback: `WaitForSignalHandler` is registered unconditionally and takes it as a required constructor dependency, so omitting it throws at DI resolution time. Use `FlowOrchestrator.SqlServer` or `FlowOrchestrator.PostgreSQL` as a reference.
- **Runtime adapter** — implement `IStepDispatcher` and (optionally) `IRecurringTriggerDispatcher` / `IRecurringTriggerInspector` / `IRecurringTriggerSync`. References, simplest first:
  - `FlowOrchestrator.InMemory` — `Channel<T>` + `PeriodicTimer`, no infra.
  - `FlowOrchestrator.Hangfire` — `IBackgroundJobClient` + `IRecurringJobManager`.
  - `FlowOrchestrator.ServiceBus` — Azure Service Bus topic + per-flow subscriptions, multi-replica safe via single-delivery scheduled messages.
- **Step handler** — implement `IStepHandler` (or extend `PollableStepHandler<TInput>` for poll-based steps). Register with `services.AddStepHandler<MyHandler>("MyStepType")`.

For a large plugin, open an issue **before** starting so we can align on naming, scope, and whether it belongs in core vs a separate package.

---

## Commit & PR flow

1. Branch from `main`. Use a short topical name: `feat/parallel-foreach`, `fix/recovery-race`, `docs/postgres-quickstart`.
2. Keep one PR = one logical change. Refactor + feature in the same PR makes review painful.
3. Before pushing:
   - `dotnet build` shows **0 errors, 0 warnings** (this also enforces `.editorconfig`).
   - `dotnet test FlowOrchestrator.UnitTests.slnx` passes.
   - If you touched scheduling, polling, or concurrency primitives, also run `dotnet test FlowOrchestrator.RegressionTests.slnx`.
4. PR title follows **Conventional Commits** — `feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `test:`. The PR title becomes the squash-merge commit message, so make it scannable.
5. Update [`CHANGELOG.md`](CHANGELOG.md) under the `## [Unreleased]` section if your change is user-facing.
6. Update docs in `docs/` if you changed public behavior or added a new article-worthy feature.

CI runs unit + integration tests on every PR. Regression tests run on push to `main` and nightly.

### Reading red CI before retrying

Roughly half of "red CI" in this repo's history has been a real regression dressed up as a flake. Always inspect the failure output (test name + assertion message + stack trace) before hitting rerun. If it looks timing-related, cross-check against the flake taxonomy above — fixing the test is cheaper than absorbing a recurring red run.

---

## What makes a great first PR

- Pick an issue labeled [`good first issue`](https://github.com/hoangsnowy/FlowOrchestrator/labels/good%20first%20issue) or [`help wanted`](https://github.com/hoangsnowy/FlowOrchestrator/labels/help%20wanted).
- Improve test coverage for an existing feature.
- Add a missing XML doc comment on a public API.
- Improve docs — typos, missing examples, broken links on the [docs site](https://hoangsnowy.github.io/FlowOrchestrator/).

If you're not sure where to start, open a Discussion and we'll suggest something matching your interests.

---

## Code of Conduct

This project follows the [Contributor Covenant v2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/). By participating, you agree to abide by its terms. Report unacceptable behavior privately to the maintainers via the channels listed in [`SECURITY.md`](SECURITY.md).

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE) that covers the project.
