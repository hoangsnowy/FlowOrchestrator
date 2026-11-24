using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// RFC 2.0 P-1 hotfixes on <see cref="PostgreSqlFlowRunStore"/>: the conditional writes the engine relies
/// on for exactly-once transitions (loop settle, manual retry) and the orphaned-claim release.
/// </summary>
public sealed partial class PostgreSqlFlowRunStoreTests
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
        Assert.Contains("iterations", step.OutputJson);
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
    public async Task ReleaseOrphanedClaimsAsync_releases_only_old_claims_without_a_status_row_and_their_ledger_rows()
    {
        // Arrange - "orphan" and "started" were claimed two minutes ago; only "started" has a row.
        // "young" was claimed just now and has no row yet.
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "OrphanFlow", runId, "manual", null, null);
        foreach (var key in new[] { "orphan", "started", "young" })
        {
            await _store.TryRecordDispatchAsync(runId, key);
            await _store.TryClaimStepAsync(runId, key);
        }

        await _store.RecordStepStartAsync(runId, "started", "Work", null, null);
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.ExecuteAsync(
                "UPDATE flow_step_claims SET claimed_at = NOW() - INTERVAL '2 minutes' WHERE run_id = @runId AND step_key IN ('orphan', 'started')",
                new { runId });
        }

        // Act
        var released = await _store.ReleaseOrphanedClaimsAsync(runId, TimeSpan.FromSeconds(60));

        // Assert
        Assert.Equal(["orphan"], released);
        var dispatched = await _store.GetDispatchedStepKeysAsync(runId);
        Assert.False(await _store.IsStepClaimedAsync(runId, "orphan"));
        Assert.DoesNotContain("orphan", dispatched);
        Assert.True(await _store.IsStepClaimedAsync(runId, "started"));
        Assert.Contains("started", dispatched);
        Assert.True(await _store.IsStepClaimedAsync(runId, "young"));
        Assert.Contains("young", dispatched);
        Assert.Empty(await _store.ReleaseOrphanedClaimsAsync(Guid.NewGuid(), TimeSpan.Zero));
    }

    [Fact]
    public async Task RecordSkippedStepAsync_writes_the_step_and_one_skipped_attempt_with_the_trace()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "SkipFlow", runId, "manual", null, null);
        await _store.TryClaimStepAsync(runId, "gated");

        // Act
        await _store.RecordSkippedStepAsync(runId, "gated", "Work", "When clause 'x' evaluated to false.", """{"expression":"x"}""");
        await _store.RecordSkippedStepAsync(runId, "plain", "Work", StepSkipReasons.PrerequisitesUnmet);

        // Assert
        var steps = (await _store.GetRunDetailAsync(runId))!.Steps!;
        var gated = steps.Single(s => s.StepKey == "gated");
        Assert.Equal("Skipped", gated.Status);
        Assert.Equal("When clause 'x' evaluated to false.", gated.ErrorMessage);
        Assert.Equal("""{"expression":"x"}""", gated.EvaluationTraceJson);
        Assert.NotNull(gated.CompletedAt);
        var attempt = Assert.Single(gated.Attempts!);
        Assert.Equal("Skipped", attempt.Status);
        Assert.Equal("""{"expression":"x"}""", attempt.EvaluationTraceJson);
        var plain = steps.Single(s => s.StepKey == "plain");
        Assert.Equal("Skipped", plain.Status);
        Assert.Equal(StepSkipReasons.PrerequisitesUnmet, plain.ErrorMessage);
        Assert.Null(plain.EvaluationTraceJson);
        Assert.Single(plain.Attempts!);
    }
}
