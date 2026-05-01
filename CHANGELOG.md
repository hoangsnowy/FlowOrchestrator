# Changelog

All notable changes to FlowOrchestrator are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.18.0] - 2026-05-01

### Added

- **`WaitForSignal` built-in step type for human-in-the-loop workflows.** A step parks
  in `StepStatus.Pending` until an external HTTP signal is delivered, then resumes with
  the signal's JSON payload as its output — available to downstream steps via
  `@steps('wait_for_approval').output.fieldName`. This unlocks the entire approval
  workflow category (order fulfillment, content moderation, contract sign-off, manual
  QA gates) without forcing users to fake it via long-polling steps.
  - New `IFlowSignalStore` abstraction (Core) with implementations for in-memory, SQL
    Server, and PostgreSQL. Migrations add `FlowSignalWaiters` / `flow_signal_waiters`
    idempotently on existing databases — no data loss.
  - New `IFlowSignalDispatcher` service plus `POST /flows/api/runs/{runId}/signals/{signalName}`
    endpoint with the documented status-code matrix (200 / 400 / 404 / 409).
  - Dashboard run-detail page renders a **Send Signal** button on every parked
    `WaitForSignal` step; the button auto-resolves the configured signal name from the
    step input so users do not need to know it.
  - Optional `timeoutSeconds` input transitions the step to `Failed` with a descriptive
    reason when no signal arrives in time. The handler is idempotent across stale
    re-dispatches: leaving the waiter row as a tombstone after delivery / expiry
    prevents the polling loop from re-registering a fresh waiter.
  - Sample: `samples/FlowOrchestrator.SampleApp/Flows/ApprovalWorkflowFlow.cs`.
  - Documentation: [WaitForSignal](docs/articles/wait-for-signal.md).

## [1.17.0] - 2026-05-01

### Added

- **`When` boolean condition on `RunAfter`**. Steps can now branch on
  *values* of prior step outputs or the trigger payload, not just on predecessor
  status. A step whose `When` evaluates to `false` is recorded as `Skipped`
  (not `Failed`), and the dashboard's new **"Why skipped"** panel shows the
  evaluation trace (e.g. `500 > 1000 → false`) so authors can debug branching
  decisions without log diving.
  - `RunAfterCondition` carries optional `Statuses` and `When`. The legacy
    `["x"] = [StepStatus.Succeeded]` array literal continues to compile via
    `[CollectionBuilder]` + an implicit conversion — no source changes required
    for existing flows.
  - Hand-rolled recursive-descent `BooleanExpressionEvaluator` supports `==`,
    `!=`, `>`, `<`, `>=`, `<=`, `&&`, `||`, `!`, parentheses, and literals
    (number, string, `true`, `false`, `null`). Zero new third-party dependencies.
  - LHS resolution reuses the existing `StepOutputResolver` (Plan 01) plus
    extracted `@triggerBody()`/`@triggerHeaders()` helpers.
  - SQL Server / PostgreSQL / in-memory storage all gain a nullable
    `EvaluationTraceJson` column (idempotent `ALTER TABLE` migration).
  - Sample: `samples/.../Flows/AmountThresholdFlow.cs`.
  - Documentation: [Conditional Execution](docs/articles/conditional-execution.md).

## [1.16.0] - 2026-04-30

### Added

- **`FlowOrchestrator.Testing` package** — fluent in-process test host for flow integration tests
  without ASP.NET, Hangfire, or a real database.
  - Single-line setup: `await FlowTestHost.For<MyFlow>().WithHandler<H>("Step").BuildAsync()`.
  - Builder options: `WithService<T>(fake)`, `WithHandler<T>(stepType)`, `WithLogging`,
    `WithSystemClock` (frozen clock for cron tests), `WithFastPolling` (collapses
    `pollIntervalSeconds` to ~100ms), `WithCustomConfiguration` (escape hatch).
  - `TriggerAsync` and `TriggerWebhookAsync` poll `IFlowRunStore` until the run reaches a
    terminal status (or the test-host timeout fires). Default timeout: 30 seconds.
  - `FlowTestRunResult` exposes `Status`, per-step `Output`/`Inputs`/`AttemptCount`/`FailureReason`,
    and the persisted `Events` log.
  - Documentation: [Testing](docs/articles/testing.md).

## [1.15.0] - 2026-04-30

### Added

- **Mermaid diagram export** (`FlowOrchestrator.Core.Diagnostics.FlowMermaidExporter`).
  Convert any `IFlowDefinition` or `FlowManifest` into a Mermaid `flowchart`
  string with `flow.ToMermaid()`. Output renders directly in GitHub READMEs,
  Notion, Confluence, and any modern Markdown surface.
  - New `MermaidExportOptions` for direction, trigger inclusion, type display, and styling.
  - Sample app now accepts `--export-mermaid <flowId|flowName>` to print the
    diagram and exit — useful for CI workflows that comment the new shape on PRs.
  - Dashboard exposes a "Mermaid" tab with a Copy button on every flow detail
    page, served by `GET /flows/api/flows/{id}/mermaid` (`text/plain`).
  - Documentation: [Mermaid Diagram Export](docs/articles/mermaid-export.md).
