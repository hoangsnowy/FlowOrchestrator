Run a full regression suite covering all existing features and vNext milestone changes (M1 + M2). Designed to be executed by an autonomous agent after any significant change.

## Scope

### Tier 1 — Core regression (always run)
These cover existing contracts that must NOT regress:

1. **Linear flow** — basic manifest with sequential steps; trigger → enqueue → execute → complete cycle
2. **Step I/O** — trigger expression resolution (`@triggerBody()`, `@triggerHeaders()`), step output wired to next step input
3. **Storage round-trips** — `IFlowStore`, `IFlowRunStore`, `IOutputsRepository` CRUD on all registered backends (InMemory, SqlServer, PostgreSQL)
4. **Hangfire integration** — `TriggerAsync`, `RunStepAsync`, `RetryStepAsync` entry points; job enqueue and execution
5. **Dashboard API contracts** — existing endpoints return same shape; GET `/flows/api/flows`, GET `/flows/api/runs`, POST `/flows/api/trigger/{flowId}/{trigger}`
6. **Polling pattern** — `PollableStepHandler<T>`: pending → reschedule, timeout → failed, condition met → succeeded

### Tier 2 — M1 new features
7. **DAG fan-out** — step with no `runAfter` runs first; multiple steps with same upstream dependency all enqueue when upstream completes
8. **DAG fan-in** — step with `runAfter` on multiple upstreams only runs when all conditions are satisfied
9. **Duplicate-enqueue guard** — when two upstream steps complete concurrently, downstream step is enqueued exactly once (idempotent claim)
10. **Completion semantics** — run ends `Succeeded` when no more runnable steps and no `Failed` steps; ends `Failed` if any `Failed` step; blocked steps marked `Skipped`
11. **Graph validation on startup** — sync fails fast on: cycle in `runAfter`, dependency points to non-existent step, empty step key
12. **ForEach expression resolution** — `forEach` field resolved from trigger body at runtime, not at definition time
13. **ForEach child keys** — each iteration generates key `{parent}.{index}.{childKey}`; keys are unique across concurrent runs
14. **ForEach ConcurrencyLimit** — at most N iterations run in parallel; remaining are queued until a slot is free
15. **Scheduler durability** — pause/resume stored in persistent store; cron override survives app restart; on startup, persisted override takes precedence over manifest default

### Tier 3 — M2 new features
16. **Run cancel** — `POST /flows/api/runs/{runId}/cancel` marks run as cancelling; new step enqueues are blocked; in-flight step allowed to finish
17. **Run timeout** — run with configured deadline that expires: execution stops, run marked with timed-out terminal state
18. **Idempotency trigger** — two triggers with same `Idempotency-Key` header within the same flow+trigger scope return the same `runId`; second call does not create a new run
19. **Event stream** — `RecordEventAsync` persists events in order; `GET /flows/api/runs/{runId}/events` returns events with correct pagination and filter
20. **OpenTelemetry** — `ActivitySource` traces span run start→end; `Meter` counters increment for success/failure/step-latency
21. **Retention cleanup** — cleanup job deletes runs/steps/events older than configured TTL; query performance does not degrade after bulk insert + cleanup cycle

## Execution protocol

### Step 1 — Baseline
```bash
dotnet build --configuration Release
dotnet test --configuration Release
```
Record baseline: total test count, pass/fail split, any existing skips.

### Step 2 — Tier 1 regression
Run existing test projects and confirm they still pass:
```bash
dotnet test ./tests/FlowOrchestrator.Core.Tests/
dotnet test ./tests/FlowOrchestrator.Hangfire.Tests/
dotnet test ./tests/FlowOrchestrator.InMemory.Tests/
dotnet test ./tests/FlowOrchestrator.SqlServer.Tests/
dotnet test ./tests/FlowOrchestrator.PostgreSQL.Tests/
dotnet test ./tests/FlowOrchestrator.Dashboard.Tests/
```

For each failure: read the failing test, trace the code path, fix root cause, rerun.

### Step 3 — Tier 2 (M1) gap analysis
Check which M1 test items (7–15 above) have test coverage:
```bash
dotnet test --filter "FullyQualifiedName~DagFanOut OR FullyQualifiedName~DagFanIn OR FullyQualifiedName~ForEach OR FullyQualifiedName~Scheduler OR FullyQualifiedName~GraphValidation"
```
For each item with no test: write tests in the appropriate test project. See test placement rules below.

### Step 4 — Tier 3 (M2) gap analysis
Check M2 coverage:
```bash
dotnet test --filter "FullyQualifiedName~Cancel OR FullyQualifiedName~Timeout OR FullyQualifiedName~Idempotency OR FullyQualifiedName~EventStream OR FullyQualifiedName~Retention"
```
For each item with no test: write tests.

### Step 5 — Full suite
```bash
dotnet test --configuration Release
```
All tests must pass. Zero failures allowed.

### Step 6 — Report
Output structured summary:
- Total tests: before → after
- New tests added: count by tier
- Failures fixed: list with root cause one-liner
- Features without coverage (if any): list with reason
- Build: frameworks × configurations, 0 errors confirmed

## Test placement rules

| Code changed in | Tests go in |
|---|---|
| `FlowOrchestrator.Core` | `tests/FlowOrchestrator.Core.Tests/` |
| `FlowOrchestrator.Hangfire` | `tests/FlowOrchestrator.Hangfire.Tests/` |
| `FlowOrchestrator.SqlServer` | `tests/FlowOrchestrator.SqlServer.Tests/` |
| `FlowOrchestrator.PostgreSQL` | `tests/FlowOrchestrator.PostgreSQL.Tests/` |
| `FlowOrchestrator.InMemory` | `tests/FlowOrchestrator.InMemory.Tests/` |
| `FlowOrchestrator.Dashboard` | `tests/FlowOrchestrator.Dashboard.Tests/` |

## Writing new tests — patterns

### DAG fan-out test skeleton
```csharp
[Fact]
public async Task DagFanOut_WhenRootCompletes_EnqueuesAllReadyChildren()
{
    // Arrange: manifest with root → [stepA, stepB] (both runAfter root)
    // Act: complete root step
    // Assert: both stepA and stepB are enqueued exactly once
}
```

### Duplicate-enqueue guard
```csharp
[Fact]
public async Task DagFanIn_WhenTwoUpstreamsCompleteConcurrently_DownstreamEnqueuedOnce()
{
    // Arrange: stepC runAfter [stepA, stepB]; complete both concurrently
    // Assert: stepC enqueued exactly once (TryClaimAsync returns false on second attempt)
}
```

### Idempotency trigger
```csharp
[Fact]
public async Task TriggerAsync_WithSameIdempotencyKey_ReturnsSameRunId()
{
    var key = Guid.NewGuid().ToString();
    var runId1 = await orchestrator.TriggerAsync(flowId, trigger, headers: new() { ["Idempotency-Key"] = key });
    var runId2 = await orchestrator.TriggerAsync(flowId, trigger, headers: new() { ["Idempotency-Key"] = key });
    runId1.Should().Be(runId2);
}
```

### Cancel cooperative
```csharp
[Fact]
public async Task CancelRun_BlocksNewStepEnqueue()
{
    // Arrange: running run
    // Act: cancel via API/store
    // Assert: attempting to enqueue next step returns without creating a Hangfire job
}
```

## What NOT to do

- Do not delete existing tests to make the suite pass — fix the root cause
- Do not skip external-dependency tests without documenting the reason in `[Trait("Category", "Integration")]`
- Do not mock storage interfaces in integration-style tests that are supposed to validate the storage behavior — use real in-memory or testcontainer implementations
- Do not mark a feature "covered" if the only test is a trivial happy-path smoke test — each M1/M2 item needs at least: happy path, edge case, and one concurrency or boundary scenario
