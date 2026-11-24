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
    /// <summary>
    /// Upper bound on update / re-read rounds in <see cref="DeliverSignalAsync"/>. Each extra round only
    /// happens when a concurrent delivery took the oldest waiter first, so it is bounded by the number of
    /// waiters registered for one signal name on one run.
    /// </summary>
    private const int MaxDeliveryAttempts = 8;

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
        var args = new { RunId = runId, SignalName = signalName, PayloadJson = payloadJson };

        // Signal names match case-insensitively, like InMemoryFlowSignalStore (OrdinalIgnoreCase) and
        // SQL Server (case-insensitive collation). lower() on the column cannot seek
        // ix_flow_signal_waiters_run_id_signal_name past its run_id prefix, which is all it needs: every
        // lookup pins run_id first and a run has a handful of waiters, so the signal_name predicate
        // filters a few rows of an index range scan.
        for (var attempt = 0; attempt < MaxDeliveryAttempts; attempt++)
        {
            // Atomic conditional update; RETURNING gives us the delivered row's identity. FOR UPDATE (no
            // SKIP LOCKED) makes a concurrent delivery of the same waiter wait for the lock holder, like
            // SQL Server. When the holder commits, PostgreSQL re-checks the row, finds it delivered and —
            // because of LIMIT 1 — returns no row instead of moving on to the next waiter; the follow-up
            // read below sorts that out. SKIP LOCKED used to report a locked waiter as missing (NotFound).
            // The expires_at predicate judges expiry by the DATABASE clock (now(), this statement's
            // transaction start) in the same statement that stamps delivered_at, with WaitForSignalHandler's
            // inclusive boundary (now >= expires_at is expired): an expired waiter never takes the payload
            // and never blocks a live one behind it, and a recorded delivered_at is always earlier than the
            // deadline. Before it, a signal landing between the deadline and the step's safety-net invocation
            // resumed the step past its timeout.
            var delivered = await conn.QueryFirstOrDefaultAsync<DeliveryRow>(
                new CommandDefinition(
                    """
                    WITH target AS (
                        SELECT run_id, step_key
                        FROM flow_signal_waiters
                        WHERE run_id = @RunId
                          AND lower(signal_name) = lower(@SignalName)
                          AND delivered_at IS NULL
                          AND (expires_at IS NULL OR expires_at > NOW())
                        ORDER BY created_at
                        LIMIT 1
                        FOR UPDATE
                    )
                    UPDATE flow_signal_waiters w
                    SET delivered_at = NOW(),
                        payload_json = @PayloadJson
                    FROM target
                    WHERE w.run_id = target.run_id AND w.step_key = target.step_key
                    RETURNING w.step_key AS StepKey, w.delivered_at AS DeliveredAt;
                    """,
                    args,
                    cancellationToken: ct)).ConfigureAwait(false);

            if (delivered is { StepKey: { } key, DeliveredAt: { } deliveredAt })
            {
                return new SignalDeliveryResult(SignalDeliveryStatus.Delivered, key, deliveredAt);
            }

            // No update happened — classify what is there. Ranked live-undelivered, expired-undelivered,
            // delivered: a live row means the update lost a race for an older waiter that a concurrent
            // delivery took, so this delivery tries the next waiter instead of giving up; an expired row
            // means Expired; a delivered row means AlreadyDelivered; no row means NotFound.
            var existing = await conn.QueryFirstOrDefaultAsync<ClassifyRow>(
                new CommandDefinition(
                    """
                    SELECT step_key AS StepKey,
                           delivered_at AS DeliveredAt,
                           (delivered_at IS NULL AND expires_at IS NOT NULL AND expires_at <= NOW()) AS IsExpired
                    FROM flow_signal_waiters
                    WHERE run_id = @RunId AND lower(signal_name) = lower(@SignalName)
                    ORDER BY (delivered_at IS NOT NULL),
                             (expires_at IS NOT NULL AND expires_at <= NOW()),
                             created_at
                    LIMIT 1;
                    """,
                    args,
                    cancellationToken: ct)).ConfigureAwait(false);

            if (existing is null)
            {
                return new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null);
            }

            if (existing.DeliveredAt is { } prior)
            {
                return new SignalDeliveryResult(SignalDeliveryStatus.AlreadyDelivered, existing.StepKey, prior);
            }

            if (existing.IsExpired)
            {
                return new SignalDeliveryResult(SignalDeliveryStatus.Expired, existing.StepKey, null);
            }
        }

        // Every attempt lost a race to a concurrent delivery of a different waiter of this signal.
        // Report what is there now rather than spin; the caller treats it like a duplicate delivery.
        return new SignalDeliveryResult(SignalDeliveryStatus.AlreadyDelivered, null, null);
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

    private sealed class ClassifyRow
    {
        public string? StepKey { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
        public bool IsExpired { get; set; }
    }
}
