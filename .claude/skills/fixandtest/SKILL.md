Implement the fix described in the user's request, then automatically write tests and verify the build.

## Steps (execute all without stopping)

1. Read the relevant source files to understand current behavior
2. Implement the fix — write the code changes now
3. Write unit tests covering:
   - The exact bug/scenario being fixed
   - Edge cases and boundary conditions
   - Regression coverage for related functionality
4. Run build: `dotnet build` — fix any errors and rebuild until clean
5. Run tests: `dotnet test` — fix any failures
6. Report: files changed (with line ranges), test count before vs after, build status, test results summary

Do not ask for confirmation between steps. Execute everything, then summarize.

## vNext-aware test patterns

When fixing bugs related to vNext features, use these patterns:

### DAG / graph planner bugs
- Test the fix against both linear flow (single `runAfter` chain) and fan-out/fan-in topology
- Always test concurrent completion (two steps completing at the same time) to catch race conditions in claim logic

### ForEach bugs
- Test empty collection (0 items), single item, and N items with ConcurrencyLimit < N
- Verify child key format: `{parent}.{index}.{childKey}` (zero-based index)

### Storage bugs
- Test the fix on InMemory store first (no infra required), then confirm SqlServer/PostgreSQL behave identically
- If the fix touches `IFlowRunStore`, also run `dotnet test ./tests/FlowOrchestrator.InMemory.Tests/`

### Cancel / timeout / idempotency bugs
- Test the cooperative cancel path: in-flight step finishes normally; no new steps enqueued after cancel
- For idempotency: test same key same scope (returns existing runId), same key different scope (creates new run)

## Test placement

| Code changed in | Tests go in |
|---|---|
| `FlowOrchestrator.Core` | `tests/FlowOrchestrator.Core.Tests/` |
| `FlowOrchestrator.Hangfire` | `tests/FlowOrchestrator.Hangfire.Tests/` |
| `FlowOrchestrator.SqlServer` | `tests/FlowOrchestrator.SqlServer.Tests/` |
| `FlowOrchestrator.PostgreSQL` | `tests/FlowOrchestrator.PostgreSQL.Tests/` |
| `FlowOrchestrator.InMemory` | `tests/FlowOrchestrator.InMemory.Tests/` |
| `FlowOrchestrator.Dashboard` | `tests/FlowOrchestrator.Dashboard.Tests/` |
