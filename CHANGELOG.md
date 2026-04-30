# Changelog

All notable changes to FlowOrchestrator are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
