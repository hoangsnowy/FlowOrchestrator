using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// Storage-contract coverage for the write the ForEach completion barrier performs when it
/// settles a loop step: <c>RecordStepCompleteAsync(runId, loopKey, "Succeeded", …)</c>.
/// </summary>
/// <remarks>
/// Every first-party <see cref="Core.Storage.IFlowRunStore"/> implements
/// <c>RecordStepCompleteAsync</c> as a pure UPDATE (SQL Server and PostgreSQL literally issue
/// <c>UPDATE FlowSteps … WHERE RunId = @RunId AND StepKey = @StepKey</c>; the in-memory store
/// mutates an existing entry and does nothing when it is absent). The barrier is therefore only
/// correct because it settles a loop whose status is <see cref="StepStatus.Running"/> — a status
/// that can only exist because <c>RecordStepStartAsync</c> already created the step row and its
/// attempt row. These tests pin both halves of that contract so a store change cannot silently
/// turn the settle write into a no-op.
/// </remarks>
public sealed class LoopBarrierStepCompletionContractTests
{
    [Fact]
    public async Task RecordStepCompleteAsync_afterRecordStepStartAsync_updatesTheStepRowAndItsLatestAttempt()
    {
        // Arrange - mirror the engine: RunStepAsync stamps the loop row Running at fan-out, and
        // the settle pass later writes Succeeded plus the iteration count.
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "LoopFlow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);

        // Act
        await store.RecordStepCompleteAsync(runId, "loop", StepStatus.Succeeded.ToString(), "{\"iterations\":3}", null);

        // Assert
        var detail = await store.GetRunDetailAsync(runId);
        var step = Assert.Single(detail!.Steps!);
        Assert.Equal("loop", step.StepKey);
        Assert.Equal(StepStatus.Succeeded.ToString(), step.Status);
        Assert.Equal("{\"iterations\":3}", step.OutputJson);
        Assert.NotNull(step.CompletedAt);

        var attempt = Assert.Single(step.Attempts!);
        Assert.Equal(StepStatus.Succeeded.ToString(), attempt.Status);
        Assert.Equal("{\"iterations\":3}", attempt.OutputJson);
        Assert.NotNull(attempt.CompletedAt);

        var statuses = await store.GetStepStatusesAsync(runId);
        Assert.Equal(StepStatus.Succeeded, statuses["loop"]);
    }

    [Fact]
    public async Task RecordStepCompleteAsync_withoutAPriorStart_createsNoRow()
    {
        // Arrange - a settle write for a key that was never started, i.e. the state the barrier's
        // "status must be Running" guard exists to make unreachable.
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "LoopFlow", runId, "manual", null, null);

        // Act
        await store.RecordStepCompleteAsync(runId, "loop", StepStatus.Succeeded.ToString(), "{\"iterations\":3}", null);

        // Assert - UPDATE-only semantics, matching the SQL Server and PostgreSQL backends: the
        // write is silently dropped rather than fabricating a completed step out of nowhere.
        var detail = await store.GetRunDetailAsync(runId);
        Assert.Empty(detail!.Steps!);
        Assert.Empty(await store.GetStepStatusesAsync(runId));
    }

    [Fact]
    public async Task RecordStepStartAsync_onASettledLoopStep_startsAFreshAttemptForTheReArmedBarrier()
    {
        // Arrange - a retried ForEach re-executes and re-arms its barrier, so the loop key must
        // accept a second Running attempt after it was already settled once.
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "LoopFlow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);
        await store.RecordStepCompleteAsync(runId, "loop", StepStatus.Succeeded.ToString(), "{\"iterations\":2}", null);

        // Act
        await store.RetryStepAsync(runId, "loop");
        await store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);
        var parked = await store.GetStepStatusesAsync(runId);
        await store.RecordStepCompleteAsync(runId, "loop", StepStatus.Succeeded.ToString(), "{\"iterations\":2}", null);

        // Assert
        Assert.Equal(StepStatus.Running, parked["loop"]);
        var detail = await store.GetRunDetailAsync(runId);
        var step = Assert.Single(detail!.Steps!);
        Assert.Equal(2, step.AttemptCount);
        Assert.Equal(2, step.Attempts!.Count);
        Assert.All(step.Attempts!, a => Assert.Equal(StepStatus.Succeeded.ToString(), a.Status));
    }
}
