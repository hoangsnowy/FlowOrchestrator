using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// 1.34.3 correctness fixes for <see cref="PostgreSqlFlowRunStore"/>: the cascade-reset scope, the
/// "exactly one winner, zero exceptions" contract of the claim / dispatch / idempotency / replay
/// gates under real concurrency, timeseries offsets, and the synthetic attempt's evaluation trace.
/// </summary>
public sealed partial class PostgreSqlFlowRunStoreTests
{
    private const int Contenders = 32;
    private const int Rounds = 8;

    /// <summary>
    /// Releases <paramref name="contenders"/> calls of <paramref name="attempt"/> at the same instant and
    /// returns every result. A thrown exception surfaces through <see cref="Task.WhenAll{TResult}(Task{TResult}[])"/>.
    /// </summary>
    private static async Task<bool[]> ContendAsync(int contenders, Func<int, Task<bool>> attempt)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task<bool>[contenders];
        for (var i = 0; i < contenders; i++)
        {
            var index = i;
            tasks[i] = Task.Run(async () =>
            {
                await gate.Task;
                return await attempt(index);
            });
        }

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ResetCascadeSkippedDependentsAsync_clears_gates_only_for_prerequisite_skipped_steps()
    {
        // Arrange — the engine passes every transitive descendant of the retried step: one already
        // Succeeded, one cascade-skipped, one When-skipped. Each holds a claim and a dispatch row.
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "CascadeFlow", runId, "manual", null, null);
        foreach (var key in new[] { "done", "cascaded", "when_skipped" })
        {
            await _store.TryClaimStepAsync(runId, key);
            await _store.TryRecordDispatchAsync(runId, key);
        }

        await _store.RecordStepStartAsync(runId, "done", "Work", null, null);
        await _store.RecordStepCompleteAsync(runId, "done", "Succeeded", """{"ok":true}""", null);
        await _store.RecordSkippedStepAsync(runId, "cascaded", "Work", StepSkipReasons.PrerequisitesUnmet);
        await _store.RecordSkippedStepAsync(runId, "when_skipped", "Work", "When condition evaluated to false.");

        // Act
        await _store.ResetCascadeSkippedDependentsAsync(runId, ["done", "cascaded", "when_skipped"]);

        // Assert — only the cascade-skipped step is reopened; the others keep their gates, so the
        // succeeded step cannot be claimed and executed a second time.
        var statuses = await _store.GetStepStatusesAsync(runId);
        var dispatched = await _store.GetDispatchedStepKeysAsync(runId);
        Assert.Equal(StepStatus.Succeeded, statuses["done"]);
        Assert.True(await _store.IsStepClaimedAsync(runId, "done"));
        Assert.Contains("done", dispatched);
        Assert.False(await _store.TryClaimStepAsync(runId, "done"));

        Assert.False(statuses.ContainsKey("cascaded"));
        Assert.False(await _store.IsStepClaimedAsync(runId, "cascaded"));
        Assert.DoesNotContain("cascaded", dispatched);

        Assert.Equal(StepStatus.Skipped, statuses["when_skipped"]);
        Assert.True(await _store.IsStepClaimedAsync(runId, "when_skipped"));
        Assert.Contains("when_skipped", dispatched);
    }

    [Fact]
    public async Task TryClaimStepAsync_under_contention_has_exactly_one_winner_and_never_throws()
    {
        // Arrange
        var runId = Guid.NewGuid();

        for (var round = 0; round < Rounds; round++)
        {
            var stepKey = $"step-{round}";

            // Act
            var results = await ContendAsync(Contenders, _ => _store.TryClaimStepAsync(runId, stepKey));

            // Assert
            Assert.Single(results, r => r);
        }
    }

    [Fact]
    public async Task TryRecordDispatchAsync_under_contention_has_exactly_one_winner_and_never_throws()
    {
        // Arrange
        var runId = Guid.NewGuid();

        for (var round = 0; round < Rounds; round++)
        {
            var stepKey = $"step-{round}";

            // Act
            var results = await ContendAsync(Contenders, _ => _store.TryRecordDispatchAsync(runId, stepKey));

            // Assert
            Assert.Single(results, r => r);
        }
    }

    [Fact]
    public async Task TryRegisterIdempotencyKeyAsync_under_contention_has_exactly_one_winner_and_never_throws()
    {
        // Arrange
        var flowId = Guid.NewGuid();

        for (var round = 0; round < Rounds; round++)
        {
            var key = $"order-{round}";

            // Act — every contender proposes its own run id for the same key.
            var results = await ContendAsync(
                Contenders,
                _ => _store.TryRegisterIdempotencyKeyAsync(flowId, "webhook", key, Guid.NewGuid()));

            // Assert
            Assert.Single(results, r => r);
        }
    }

    [Fact]
    public async Task WebhookReplayStore_TryRegisterAsync_under_contention_has_exactly_one_winner_and_never_throws()
    {
        // Arrange
        var replay = new PostgreSqlWebhookReplayStore(_connectionString);
        var flowId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        for (var round = 0; round < Rounds; round++)
        {
            var nonce = $"nonce-{round}";

            // Act
            var results = await ContendAsync(
                Contenders,
                _ => replay.TryRegisterAsync(flowId, "webhook", nonce, expiresAt).AsTask());

            // Assert
            Assert.Single(results, r => r);
        }
    }

    [Fact]
    public async Task GetRunTimeseriesAsync_accepts_a_non_utc_window()
    {
        // Arrange — the dashboard forwards whatever offset the browser sent; Npgsql refuses to bind a
        // non-UTC DateTimeOffset to timestamptz, which surfaced as a dashboard 500.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "TsFlow", runId, "manual", null, null);
        var offset = TimeSpan.FromHours(7);
        var until = DateTimeOffset.UtcNow.AddHours(1).ToOffset(offset);
        var since = until.AddHours(-4);

        // Act
        var buckets = await _store.GetRunTimeseriesAsync(RunTimeseriesGranularity.Hour, since, until, flowId);

        // Assert
        Assert.Equal(1, buckets.Sum(b => b.Total));
    }

    [Fact]
    public async Task GetRunDetailAsync_synthetic_attempt_carries_the_evaluation_trace()
    {
        // Arrange — a step row without attempt history (written before attempts were tracked), so
        // run detail synthesises its single attempt from the step row.
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "TraceFlow", runId, "manual", null, null);
        const string trace = """{"expression":"@triggerBody()?.go == true","result":false}""";
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO flow_steps (run_id, step_key, step_type, status, error_message, started_at, completed_at, evaluation_trace_json)
                VALUES (@RunId, 'gated', 'Work', 'Skipped', 'When condition evaluated to false.', NOW(), NOW(), @Trace)
                """,
                new { RunId = runId, Trace = trace });
        }

        // Act
        var detail = await _store.GetRunDetailAsync(runId);

        // Assert
        var step = Assert.Single(detail!.Steps!);
        var attempt = Assert.Single(step.Attempts!);
        Assert.Equal(trace, step.EvaluationTraceJson);
        Assert.Equal(trace, attempt.EvaluationTraceJson);
    }
}
