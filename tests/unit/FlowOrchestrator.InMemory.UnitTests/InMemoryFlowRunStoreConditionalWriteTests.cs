using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// RFC 2.0 P-1 hotfixes on <see cref="InMemoryFlowRunStore"/>: the conditional writes the engine relies
/// on for exactly-once transitions (loop settle, manual retry) and the orphaned-claim release.
/// </summary>
public sealed class InMemoryFlowRunStoreConditionalWriteTests
{
    private readonly InMemoryFlowRunStore _sut = new();

    /// <summary>
    /// Releases <paramref name="contenders"/> calls of <paramref name="attempt"/> at the same instant and
    /// returns every result.
    /// </summary>
    private static async Task<bool[]> ContendAsync(int contenders, Func<Task<bool>> attempt)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task<bool>[contenders];
        for (var i = 0; i < contenders; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await gate.Task;
                return await attempt();
            });
        }

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task TryCompleteRunningStepAsync_MovesARunningStepOnce_ThenReportsFalse()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "loop", "ForEach", null, null);

        // Act
        var first = await _sut.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", """{"iterations":2}""", null);
        var second = await _sut.TryCompleteRunningStepAsync(runId, "loop", "Skipped", null, "Run is Cancelled.");

        // Assert
        Assert.True(first);
        Assert.False(second);
        var step = await _sut.GetStepAsync(runId, "loop");
        Assert.Equal("Succeeded", step!.Status);
        Assert.Equal("""{"iterations":2}""", step.OutputJson);
        Assert.NotNull(step.CompletedAt);
    }

    [Fact]
    public async Task TryCompleteRunningStepAsync_LeavesATerminalOrMissingStepUntouched()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "loop", "ForEach", null, null);
        await _sut.RecordStepCompleteAsync(runId, "loop", "Skipped", null, "Run is Cancelled.");

        // Act
        var terminal = await _sut.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", null, null);
        var missing = await _sut.TryCompleteRunningStepAsync(runId, "never_started", "Succeeded", null, null);

        // Assert
        Assert.False(terminal);
        Assert.False(missing);
        Assert.Equal("Skipped", (await _sut.GetStepAsync(runId, "loop"))!.Status);
    }

    [Fact]
    public async Task TryCompleteRunningStepAsync_UnderContention_HasExactlyOneWinner()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "loop", "ForEach", null, null);

        // Act
        var results = await ContendAsync(64, () =>
            _sut.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", null, null));

        // Assert
        Assert.Equal(1, results.Count(won => won));
        Assert.Equal(StepStatus.Succeeded, (await _sut.GetStepStatusesAsync(runId))["loop"]);
    }
}
