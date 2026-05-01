using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Dapper-based SQL Server implementation of <see cref="IFlowSignalStore"/>.
/// Persists waiters in the <c>FlowSignalWaiters</c> table created by <see cref="FlowOrchestratorSqlMigrator"/>.
/// </summary>
internal sealed class SqlFlowSignalStore : IFlowSignalStore
{
    private readonly string _connectionString;

    public SqlFlowSignalStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async ValueTask RegisterWaiterAsync(
        Guid runId,
        string stepKey,
        string signalName,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                MERGE [FlowSignalWaiters] AS target
                USING (SELECT @RunId AS RunId, @StepKey AS StepKey) AS source
                ON target.RunId = source.RunId AND target.StepKey = source.StepKey
                WHEN MATCHED THEN
                    UPDATE SET SignalName = @SignalName, ExpiresAt = @ExpiresAt
                WHEN NOT MATCHED THEN
                    INSERT (RunId, StepKey, SignalName, CreatedAt, ExpiresAt)
                    VALUES (@RunId, @StepKey, @SignalName, SYSDATETIMEOFFSET(), @ExpiresAt);
                """,
                new
                {
                    RunId = runId,
                    StepKey = stepKey,
                    SignalName = signalName,
                    ExpiresAt = expiresAt
                },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    public async ValueTask<SignalDeliveryResult> DeliverSignalAsync(
        Guid runId,
        string signalName,
        string payloadJson,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);

        // Atomic: only update the row if no payload has been delivered yet.
        // Returns 1 row when delivered, 0 rows when (a) no waiter matches or (b) already delivered.
        var rows = await conn.QueryAsync<DeliveryRow>(
            new CommandDefinition(
                """
                ;WITH target AS (
                    SELECT TOP (1) *
                    FROM [FlowSignalWaiters]
                    WHERE RunId = @RunId AND SignalName = @SignalName
                    ORDER BY CreatedAt
                )
                UPDATE target
                SET DeliveredAt = SYSDATETIMEOFFSET(),
                    PayloadJson = @PayloadJson
                OUTPUT inserted.StepKey, inserted.DeliveredAt
                WHERE target.DeliveredAt IS NULL;
                """,
                new
                {
                    RunId = runId,
                    SignalName = signalName,
                    PayloadJson = payloadJson
                },
                cancellationToken: ct)).ConfigureAwait(false);

        var delivered = rows.FirstOrDefault();
        if (delivered is { StepKey: { } stepKey, DeliveredAt: { } deliveredAt })
        {
            return new SignalDeliveryResult(SignalDeliveryStatus.Delivered, stepKey, deliveredAt);
        }

        // No update happened — figure out whether the waiter is missing or already delivered.
        var existing = await conn.QueryFirstOrDefaultAsync<DeliveryRow>(
            new CommandDefinition(
                """
                SELECT TOP (1) StepKey, DeliveredAt
                FROM [FlowSignalWaiters]
                WHERE RunId = @RunId AND SignalName = @SignalName
                ORDER BY CreatedAt;
                """,
                new { RunId = runId, SignalName = signalName },
                cancellationToken: ct)).ConfigureAwait(false);

        if (existing is { DeliveredAt: { } prior })
        {
            return new SignalDeliveryResult(SignalDeliveryStatus.AlreadyDelivered, existing.StepKey, prior);
        }

        return new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null);
    }

    public async ValueTask<FlowSignalWaiter?> GetWaiterAsync(
        Guid runId,
        string stepKey,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<FlowSignalWaiter>(
            new CommandDefinition(
                """
                SELECT RunId, StepKey, SignalName, CreatedAt, ExpiresAt, DeliveredAt, PayloadJson
                FROM [FlowSignalWaiters]
                WHERE RunId = @RunId AND StepKey = @StepKey;
                """,
                new { RunId = runId, StepKey = stepKey },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    public async ValueTask RemoveWaiterAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM [FlowSignalWaiters] WHERE RunId = @RunId AND StepKey = @StepKey;",
                new { RunId = runId, StepKey = stepKey },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    private sealed class DeliveryRow
    {
        public string? StepKey { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
    }
}
