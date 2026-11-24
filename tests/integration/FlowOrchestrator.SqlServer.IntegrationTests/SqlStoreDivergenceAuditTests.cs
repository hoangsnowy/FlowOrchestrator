using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// Storage-contract audit: behaviours where the SQL Server stores diverge from the in-memory and
/// PostgreSQL providers. Each test states the parity expectation; a failure is the defect.
/// </summary>
/// <remarks>
/// The step-key tests depend on the database's default collation. The Testcontainers image uses the
/// SQL Server default <c>SQL_Latin1_General_CP1_CI_AS</c>, which is also what most production servers run.
/// </remarks>
public sealed class SqlStoreDivergenceAuditTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlFlowRunStore _runs;
    private readonly SqlFlowScheduleStateStore _schedules;

    public SqlStoreDivergenceAuditTests(SqlServerFixture fixture)
    {
        _runs = new SqlFlowRunStore(fixture.ConnectionString);
        _schedules = new SqlFlowScheduleStateStore(fixture.ConnectionString);
    }

    [Fact]
    public async Task TryClaimStep_treats_step_keys_that_differ_only_by_case_as_distinct_steps()
    {
        // Arrange — StepCollection is an ordinal Dictionary and FlowGraphPlanner.Validate accepts both
        // keys, so "Notify" and "notify" are two steps. InMemory and PostgreSQL claim them separately.
        var runId = Guid.NewGuid();
        await _runs.StartRunAsync(Guid.NewGuid(), "CaseFlow", runId, "manual", null, null);
        Assert.True(await _runs.TryClaimStepAsync(runId, "Notify"));

        // Act
        var secondClaimed = await _runs.TryClaimStepAsync(runId, "notify");

        // Assert
        Assert.True(secondClaimed);
    }

    [Fact]
    public async Task TryRecordDispatch_treats_step_keys_that_differ_only_by_case_as_distinct_steps()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _runs.StartRunAsync(Guid.NewGuid(), "CaseFlow", runId, "manual", null, null);
        Assert.True(await _runs.TryRecordDispatchAsync(runId, "Notify"));

        // Act
        var secondRecorded = await _runs.TryRecordDispatchAsync(runId, "notify");

        // Assert
        Assert.True(secondRecorded);
    }

    [Fact]
    public async Task GetStepStatuses_reports_both_steps_whose_keys_differ_only_by_case()
    {
        // Arrange — both steps start and one fails. On a case-insensitive PK the second start MERGEs
        // into the first step's row, so the planner never sees "notify" at all.
        var runId = Guid.NewGuid();
        await _runs.StartRunAsync(Guid.NewGuid(), "CaseFlow", runId, "manual", null, null);
        await _runs.RecordStepStartAsync(runId, "Notify", "Work", null, null);
        await _runs.RecordStepCompleteAsync(runId, "Notify", nameof(StepStatus.Succeeded), null, null);
        await _runs.RecordStepStartAsync(runId, "notify", "Work", null, null);

        // Act
        var statuses = await _runs.GetStepStatusesAsync(runId);

        // Assert
        Assert.Equal(2, statuses.Count);
        Assert.Equal(StepStatus.Succeeded, statuses["Notify"]);
        Assert.Equal(StepStatus.Running, statuses["notify"]);
    }

    [Fact]
    public async Task ScheduleState_SaveAsync_concurrent_first_saves_of_one_job_never_throw()
    {
        // Arrange — two dashboard replicas pausing the same never-saved schedule at once. The MERGE has
        // no HOLDLOCK, so both can see "not matched" and the loser hits PK_FlowScheduleStates
        // (SqlFlowStore.SaveAsync documents and fixes exactly this race; this store does not).
        const int rounds = 8;
        const int contenders = 16;
        var flowId = Guid.NewGuid();

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            for (var round = 0; round < rounds; round++)
            {
                var jobId = $"audit-{Guid.NewGuid():N}";
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var tasks = Enumerable.Range(0, contenders).Select(i => Task.Run(async () =>
                {
                    await gate.Task;
                    await _schedules.SaveAsync(new FlowScheduleState
                    {
                        JobId = jobId,
                        FlowId = flowId,
                        FlowName = "AuditFlow",
                        TriggerKey = "schedule",
                        IsPaused = i % 2 == 0,
                    });
                })).ToArray();
                gate.SetResult();
                await Task.WhenAll(tasks);
            }
        });

        // Assert
        Assert.Null(failure);
    }

    [Fact]
    public async Task TryRegisterIdempotencyKey_accepts_a_caller_supplied_key_longer_than_256_chars()
    {
        // Arrange — the key is the raw value of the client's Idempotency-Key header; nothing upstream
        // caps its length. InMemory accepts it; the column is NVARCHAR(256), so the INSERT fails with
        // "String or binary data would be truncated" and the trigger surfaces a 500 to the caller.
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
