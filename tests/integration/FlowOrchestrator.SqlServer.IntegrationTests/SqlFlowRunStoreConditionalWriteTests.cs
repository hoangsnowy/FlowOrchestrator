using Dapper;
using FlowOrchestrator.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// RFC 2.0 P-1 hotfixes on <see cref="SqlFlowRunStore"/>: the conditional writes the engine relies on
/// for exactly-once transitions (loop settle, manual retry) and the orphaned-claim release.
/// </summary>
public sealed partial class SqlFlowRunStoreTests
{
    [Fact]
    public async Task TryCompleteRunningStepAsync_moves_a_Running_step_once_and_then_reports_false()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
        await _store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);

        // Act
        var first = await _store.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", """{"iterations":2}""", null);
        var second = await _store.TryCompleteRunningStepAsync(runId, "loop", "Skipped", null, "Run is Cancelled.");

        // Assert
        Assert.True(first);
        Assert.False(second);
        var step = await _store.GetStepAsync(runId, "loop");
        Assert.Equal("Succeeded", step!.Status);
        Assert.Equal("""{"iterations":2}""", step.OutputJson);
        Assert.NotNull(step.CompletedAt);
        var attempt = Assert.Single((await _store.GetRunDetailAsync(runId))!.Steps!.Single(s => s.StepKey == "loop").Attempts!);
        Assert.Equal("Succeeded", attempt.Status);
    }

    [Fact]
    public async Task TryCompleteRunningStepAsync_leaves_a_terminal_or_missing_step_untouched()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
        await _store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);
        await _store.RecordStepCompleteAsync(runId, "loop", "Skipped", null, "Run is Cancelled.");

        // Act
        var terminal = await _store.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", """{"iterations":2}""", null);
        var missing = await _store.TryCompleteRunningStepAsync(runId, "never_started", "Succeeded", null, null);

        // Assert
        Assert.False(terminal);
        Assert.False(missing);
        var step = await _store.GetStepAsync(runId, "loop");
        Assert.Equal("Skipped", step!.Status);
        Assert.Equal("Run is Cancelled.", step.ErrorMessage);
        Assert.Null(await _store.GetStepAsync(runId, "never_started"));
    }

    [Fact]
    public async Task TryCompleteRunningStepAsync_under_contention_has_exactly_one_winner_and_never_throws()
    {
        for (var round = 0; round < Rounds; round++)
        {
            // Arrange
            var runId = Guid.NewGuid();
            await _store.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
            await _store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);

            // Act
            var results = await ContendAsync(Contenders, _ =>
                _store.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", """{"iterations":2}""", null));

            // Assert
            Assert.Equal(1, results.Count(won => won));
            Assert.Equal(StepStatus.Succeeded, (await _store.GetStepStatusesAsync(runId))["loop"]);
        }
    }

    [Fact]
    public async Task TryCompleteRunningStepAsync_with_the_token_index_indexes_only_the_winning_write()
    {
        // Arrange
        var store = TokenStore();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);

        // Act
        var won = await store.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", """{"note":"winnerpayload"}""", null);
        var lost = await store.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", """{"note":"loserpayload"}""", null);

        // Assert
        Assert.True(won);
        Assert.False(lost);
        await using var conn = new SqlConnection(_connectionString);
        var tokens = (await conn.QueryAsync<string>(
            "SELECT Token FROM FlowRunSearchTokens WHERE RunId = @runId", new { runId })).ToList();
        Assert.Contains("winnerpayload", tokens);
        Assert.DoesNotContain("loserpayload", tokens);
    }
}
