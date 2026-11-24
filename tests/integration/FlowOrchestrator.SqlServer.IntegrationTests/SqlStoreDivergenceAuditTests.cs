using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// Storage-contract audit: behaviours where the SQL Server stores diverge from the in-memory and
/// PostgreSQL providers. Each test states the parity expectation; a failure is the defect.
/// </summary>
/// <remarks>
/// The step-key characterisation test depends on the database's default collation. The Testcontainers image uses the
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
    public async Task StepKey_primary_keys_compare_case_insensitively_under_the_default_collation()
    {
        // Arrange — characterisation of the schema: every StepKey column (FlowSteps, FlowStepClaims,
        // FlowStepDispatches, FlowOutputs.[Key]) inherits the database's case-insensitive collation, so
        // "Notify" and "notify" are one row. This is why FlowGraphPlanner.Validate rejects such a pair.
        var runId = Guid.NewGuid();
        await _runs.StartRunAsync(Guid.NewGuid(), "CaseFlow", runId, "manual", null, null);
        Assert.True(await _runs.TryClaimStepAsync(runId, "Notify"));
        Assert.True(await _runs.TryRecordDispatchAsync(runId, "Notify"));

        // Act
        var secondClaimed = await _runs.TryClaimStepAsync(runId, "notify");
        var secondRecorded = await _runs.TryRecordDispatchAsync(runId, "notify");

        // Assert
        Assert.False(secondClaimed);
        Assert.False(secondRecorded);
    }

    [Fact]
    public void Validate_rejects_step_keys_that_differ_only_by_case_so_the_collision_cannot_reach_the_store()
    {
        // Arrange — the manifest the original audit used: two steps the ordinal StepCollection treats as
        // distinct. Validation runs in FlowSyncHostedService before the flow is persisted, so a run of
        // this flow can never be started against the case-insensitive tables above.
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["Notify"] = new StepMetadata { Type = "Work" },
                ["notify"] = new StepMetadata { Type = "Work" },
            },
        });

        // Act
        var result = new FlowGraphPlanner().Validate(flow);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("differ only by case", StringComparison.Ordinal));
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
