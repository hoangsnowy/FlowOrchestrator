using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// Dapper-based PostgreSQL implementation of <see cref="IFlowSignalStore"/>.
/// Persists waiters in the <c>flow_signal_waiters</c> table created by <see cref="PostgreSqlFlowOrchestratorMigrator"/>.
/// </summary>
public sealed class PostgreSqlFlowSignalStore : IFlowSignalStore
{
    private readonly string _connectionString;

    public PostgreSqlFlowSignalStore(string connectionString)
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
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO flow_signal_waiters (run_id, step_key, signal_name, created_at, expires_at)
                VALUES (@RunId, @StepKey, @SignalName, NOW(), @ExpiresAt)
                ON CONFLICT (run_id, step_key) DO UPDATE
                SET signal_name = EXCLUDED.signal_name,
                    expires_at  = EXCLUDED.expires_at;
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
        await using var conn = new NpgsqlConnection(_connectionString);

        // Atomic conditional update; RETURNING gives us the delivered row's identity. The CTE
        // narrows to the single oldest waiter so concurrent deliveries can never race past each other.
        var delivered = await conn.QueryFirstOrDefaultAsync<DeliveryRow>(
            new CommandDefinition(
                """
                WITH target AS (
                    SELECT run_id, step_key
                    FROM flow_signal_waiters
                    WHERE run_id = @RunId
                      AND signal_name = @SignalName
                      AND delivered_at IS NULL
                    ORDER BY created_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE flow_signal_waiters w
                SET delivered_at = NOW(),
                    payload_json = @PayloadJson
                FROM target
                WHERE w.run_id = target.run_id AND w.step_key = target.step_key
                RETURNING w.step_key AS StepKey, w.delivered_at AS DeliveredAt;
                """,
                new
                {
                    RunId = runId,
                    SignalName = signalName,
                    PayloadJson = payloadJson
                },
                cancellationToken: ct)).ConfigureAwait(false);

        if (delivered is { StepKey: { } key, DeliveredAt: { } deliveredAt })
        {
            return new SignalDeliveryResult(SignalDeliveryStatus.Delivered, key, deliveredAt);
        }

        // No update happened — distinguish missing waiter from already-delivered waiter.
        var existing = await conn.QueryFirstOrDefaultAsync<DeliveryRow>(
            new CommandDefinition(
                """
                SELECT step_key AS StepKey, delivered_at AS DeliveredAt
                FROM flow_signal_waiters
                WHERE run_id = @RunId AND signal_name = @SignalName
                ORDER BY created_at
                LIMIT 1;
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
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<FlowSignalWaiter>(
            new CommandDefinition(
                """
                SELECT run_id     AS RunId,
                       step_key   AS StepKey,
                       signal_name AS SignalName,
                       created_at  AS CreatedAt,
                       expires_at  AS ExpiresAt,
                       delivered_at AS DeliveredAt,
                       payload_json AS PayloadJson
                FROM flow_signal_waiters
                WHERE run_id = @RunId AND step_key = @StepKey;
                """,
                new { RunId = runId, StepKey = stepKey },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    public async ValueTask RemoveWaiterAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM flow_signal_waiters WHERE run_id = @RunId AND step_key = @StepKey;",
                new { RunId = runId, StepKey = stepKey },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    private sealed class DeliveryRow
    {
        public string? StepKey { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
    }
}
