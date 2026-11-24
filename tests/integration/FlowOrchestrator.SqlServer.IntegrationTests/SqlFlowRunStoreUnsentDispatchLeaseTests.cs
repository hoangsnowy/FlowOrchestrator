using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// <see cref="SqlFlowRunStore.LeaseUnsentDispatchesAsync"/>: which dispatch-ledger rows count as a hand-off
/// that never returned, that a lease is handed out once per timeout window, and that concurrent callers (one
/// per replica) never lease the same row twice.
/// </summary>
/// <remarks>
/// The database is shared with the other store tests, so every assertion filters the lease to this test's run.
/// </remarks>
public sealed partial class SqlFlowRunStoreTests
{
    private static readonly TimeSpan UnsentTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task LeaseUnsentDispatchesAsync_leases_only_old_unannotated_unclaimed_rows_with_no_status_or_a_Pending_one_on_a_running_run()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "LeaseFlow", runId, "manual", null, null);
        foreach (var key in new[] { "unsent", "annotated", "claimed", "done", "polling", "young" })
        {
            await _store.TryRecordDispatchAsync(runId, key);
        }

        await _store.AnnotateDispatchAsync(runId, "annotated", "job-1");
        await _store.TryClaimStepAsync(runId, "claimed");
        await _store.RecordStepStartAsync(runId, "done", "Work", null, null);
        await _store.RecordStepCompleteAsync(runId, "done", "Succeeded", null, null);
        await _store.RecordStepStartAsync(runId, "polling", "Poll", null, null);
        await _store.RecordStepCompleteAsync(runId, "polling", "Pending", null, null);

        var closedRun = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "LeaseFlow", closedRun, "manual", null, null);
        await _store.TryRecordDispatchAsync(closedRun, "unsent");
        await _store.CompleteRunAsync(closedRun, "Cancelled");
        await AgeDispatchesAsync([runId, closedRun], except: "young");

        // Act
        var leased = Mine(await _store.LeaseUnsentDispatchesAsync(UnsentTimeout, 1000), runId, closedRun);

        // Assert
        Assert.Equal(["polling", "unsent"], leased.Select(l => l.StepKey).Order(StringComparer.Ordinal));
        Assert.All(leased, l => Assert.Equal((runId, flowId), (l.RunId, l.FlowId)));
    }

    [Fact]
    public async Task LeaseUnsentDispatchesAsync_does_not_lease_a_row_again_inside_the_window_and_an_annotation_ends_it()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "LeaseFlow", runId, "manual", null, null);
        await _store.TryRecordDispatchAsync(runId, "a");
        await _store.TryRecordDispatchAsync(runId, "b");
        await AgeDispatchesAsync([runId]);

        // Act
        var first = Mine(await _store.LeaseUnsentDispatchesAsync(UnsentTimeout, 1000), runId);
        var immediately = Mine(await _store.LeaseUnsentDispatchesAsync(UnsentTimeout, 1000), runId);
        await _store.AnnotateDispatchAsync(runId, "a", "redelivered");
        await AgeDispatchesAsync([runId]);
        var aWindowLater = Mine(await _store.LeaseUnsentDispatchesAsync(UnsentTimeout, 1000), runId);

        // Assert
        Assert.Equal(2, first.Count);
        Assert.Empty(immediately);
        Assert.Equal("b", Assert.Single(aWindowLater).StepKey);
        Assert.Contains("a", await _store.GetDispatchedStepKeysAsync(runId));
    }

    [Fact]
    public async Task LeaseUnsentDispatchesAsync_under_contention_hands_each_row_to_exactly_one_caller()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "LeaseFlow", runId, "manual", null, null);
        for (var i = 0; i < 40; i++)
        {
            await _store.TryRecordDispatchAsync(runId, "s" + i);
        }

        await AgeDispatchesAsync([runId]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return await _store.LeaseUnsentDispatchesAsync(UnsentTimeout, 1000);
            }))
            .ToArray();

        // Act
        gate.SetResult();
        var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(60));

        // Assert
        var keys = results.SelectMany(r => Mine(r, runId)).Select(l => l.StepKey).ToList();
        Assert.Equal(40, keys.Count);
        Assert.Equal(40, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_migrator_creates_the_filtered_unsent_dispatch_index()
    {
        // Arrange
        await using var conn = new SqlConnection(_connectionString);

        // Act
        var filter = await conn.ExecuteScalarAsync<string?>(
            "SELECT filter_definition FROM sys.indexes WHERE name = 'IX_FlowStepDispatches_Unsent' AND object_id = OBJECT_ID(N'[FlowStepDispatches]')");

        // Assert
        Assert.NotNull(filter);
        Assert.Contains("DispatchJobId", filter);
        Assert.Contains("IS NULL", filter, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AgeDispatchesAsync(Guid[] runIds, string? except = null)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE FlowStepDispatches SET DispatchedAt = DATEADD(MINUTE, -2, SYSDATETIMEOFFSET()) WHERE RunId IN @runIds AND (@except IS NULL OR StepKey <> @except)",
            new { runIds, except });
    }

    private static List<UnsentDispatch> Mine(IReadOnlyList<UnsentDispatch> leased, params Guid[] runIds) =>
        leased.Where(l => runIds.Contains(l.RunId)).ToList();
}
