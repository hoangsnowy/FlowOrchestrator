using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Storage-contract audit: behaviours where <see cref="PostgreSqlFlowSignalStore"/> and
/// <see cref="PostgreSqlFlowRunStore"/> diverge from the in-memory and SQL Server providers.
/// Each test states the parity expectation; a failure is the defect.
/// </summary>
public sealed class PostgreSqlStoreDivergenceAuditTests : IClassFixture<PostgreSqlFixture>
{
    private readonly string _connectionString;
    private readonly PostgreSqlFlowSignalStore _signals;
    private readonly PostgreSqlFlowRunStore _runs;

    public PostgreSqlStoreDivergenceAuditTests(PostgreSqlFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
        _signals = new PostgreSqlFlowSignalStore(fixture.ConnectionString);
        _runs = new PostgreSqlFlowRunStore(fixture.ConnectionString);
    }

    [Fact]
    public async Task DeliverSignal_while_another_delivery_of_the_same_waiter_is_uncommitted_reports_AlreadyDelivered_not_NotFound()
    {
        // Arrange — a waiter exists, and a concurrent deliverer holds its row lock with the delivery
        // written but not yet committed (the state a racing DeliverSignalAsync is in mid-statement).
        var runId = Guid.NewGuid();
        await _signals.RegisterWaiterAsync(runId, "wait", "approval", null);

        await using var holder = new NpgsqlConnection(_connectionString);
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(
            "UPDATE flow_signal_waiters SET delivered_at = NOW(), payload_json = '{}' WHERE run_id = @RunId AND step_key = 'wait'",
            new { RunId = runId }, tx);

        // Act — the duplicate delivery runs while the first one is in flight. A correct store either
        // waits for the lock holder or reports the waiter as taken; it must never claim the waiter is absent.
        var duplicate = _signals.DeliverSignalAsync(runId, "approval", "{}").AsTask();
        // Not a timing assertion: give the duplicate a chance to reach the row before the holder commits.
        // If it has not got there yet, it sees the committed delivery and the assertion still holds.
        await Task.WhenAny(duplicate, Task.Delay(TimeSpan.FromSeconds(2)));
        await tx.CommitAsync();
        var result = await duplicate.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(SignalDeliveryStatus.AlreadyDelivered, result.Status);
    }

    [Fact]
    public async Task DeliverSignal_matches_signal_name_case_insensitively_like_the_other_stores()
    {
        // Arrange — InMemoryFlowSignalStore compares with OrdinalIgnoreCase and SQL Server's default
        // collation is case-insensitive, so both deliver this. PostgreSQL compares signal_name with '='.
        var runId = Guid.NewGuid();
        await _signals.RegisterWaiterAsync(runId, "wait", "Approval", null);

        // Act
        var result = await _signals.DeliverSignalAsync(runId, "approval", "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.Delivered, result.Status);
        Assert.Equal("wait", result.StepKey);
    }

    [Fact]
    public async Task SearchRuns_status_filter_is_case_insensitive_like_the_other_stores()
    {
        // Arrange — InMemory compares Status with OrdinalIgnoreCase and SQL Server's column collation
        // is case-insensitive; PostgreSQL compares status with '='.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _runs.StartRunAsync(flowId, "CaseFlow", runId, "manual", null, null);
        await _runs.CompleteRunAsync(runId, "Failed");

        // Act
        var page = await _runs.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Status = "failed" });
        var legacy = await _runs.GetRunsPageAsync(flowId, "failed", 0, 50, null);

        // Assert
        Assert.Contains(page.Runs, r => r.Id == runId);
        Assert.Contains(legacy.Runs, r => r.Id == runId);
    }

    [Fact]
    public async Task TryRegisterIdempotencyKey_accepts_a_caller_supplied_key_longer_than_256_chars()
    {
        // Arrange — the key is the raw value of the client's Idempotency-Key header; nothing upstream
        // caps its length. InMemory accepts it; the column is VARCHAR(256), so the INSERT fails with
        // 22001 "value too long for type character varying(256)" and the trigger surfaces a 500 to the caller.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var key = new string('k', 300);

        // Act
        var registered = await _runs.TryRegisterIdempotencyKeyAsync(flowId, "webhook", key, runId);
        var found = await _runs.FindRunIdByIdempotencyKeyAsync(flowId, "webhook", key);

        // Assert
        Assert.True(registered);
        Assert.Equal(runId, found);
    }
}
