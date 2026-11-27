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
    /// <summary>
    /// Upper bound on update / re-read rounds in <see cref="DeliverSignalAsync"/>, as in the PostgreSQL store.
    /// An extra round only happens when the follow-up read finds a live undelivered waiter the update did
    /// not take — a concurrent delivery or registration in between — so it is bounded by the number of
    /// waiters registered for one signal name on one run.
    /// </summary>
    private const int MaxDeliveryAttempts = 8;

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
        // HOLDLOCK, UPDLOCK: see SqlFlowStore.SaveAsync — without them a concurrent first registration of
        // the same (run, step) can race past the match probe into a PK violation.
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                MERGE [FlowSignalWaiters] WITH (HOLDLOCK, UPDLOCK) AS target
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
        var args = new { RunId = runId, SignalName = signalName, PayloadJson = payloadJson };

        for (var attempt = 0; attempt < MaxDeliveryAttempts; attempt++)
        {
            // Atomic conditional update. The CTE narrows to the single OLDEST UNDELIVERED, LIVE waiter.
            // `DeliveredAt IS NULL` is essential: without it, a second waiter for the same RunId+SignalName
            // never receives the signal, because the CTE keeps re-selecting the first — already-delivered —
            // row. The ExpiresAt predicate judges expiry by the DATABASE clock in the same statement that
            // stamps DeliveredAt, with WaitForSignalHandler's inclusive boundary (now >= ExpiresAt is
            // expired), so a recorded DeliveredAt is always earlier than the deadline and an expired waiter
            // neither takes the payload nor blocks a live one behind it. Before this filter a signal landing
            // between the deadline and the step's safety-net invocation resumed the step past its timeout.
            var rows = await conn.QueryAsync<DeliveryRow>(
                new CommandDefinition(
                    """
                    ;WITH target AS (
                        SELECT TOP (1) *
                        FROM [FlowSignalWaiters]
                        WHERE RunId = @RunId AND SignalName = @SignalName
                          AND DeliveredAt IS NULL
                          AND (ExpiresAt IS NULL OR ExpiresAt > SYSDATETIMEOFFSET())
                        ORDER BY CreatedAt
                    )
                    UPDATE target
                    SET DeliveredAt = SYSDATETIMEOFFSET(),
                        PayloadJson = @PayloadJson
                    OUTPUT inserted.StepKey, inserted.DeliveredAt;
                    """,
                    args,
                    cancellationToken: ct)).ConfigureAwait(false);

            var delivered = rows.FirstOrDefault();
            if (delivered is { StepKey: { } stepKey, DeliveredAt: { } deliveredAt })
            {
                return new SignalDeliveryResult(SignalDeliveryStatus.Delivered, stepKey, deliveredAt);
            }

            // No update happened — classify what is there. Ranked live-undelivered, expired-undelivered,
            // delivered, so the answer names the waiter the signal was meant for: a live row means the update
            // lost a race (a concurrent delivery took the waiter it had selected, or the waiter registered in
            // between) and the loop tries again; an expired row means Expired; a delivered row means
            // AlreadyDelivered; no row means NotFound.
            var existing = await conn.QueryFirstOrDefaultAsync<ClassifyRow>(
                new CommandDefinition(
                    """
                    SELECT TOP (1) StepKey, DeliveredAt,
                           CASE WHEN DeliveredAt IS NULL AND ExpiresAt IS NOT NULL AND ExpiresAt <= SYSDATETIMEOFFSET()
                                THEN 1 ELSE 0 END AS IsExpired
                    FROM [FlowSignalWaiters]
                    WHERE RunId = @RunId AND SignalName = @SignalName
                    ORDER BY CASE
                                 WHEN DeliveredAt IS NOT NULL THEN 2
                                 WHEN ExpiresAt IS NOT NULL AND ExpiresAt <= SYSDATETIMEOFFSET() THEN 1
                                 ELSE 0
                             END,
                             CreatedAt;
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

            if (existing.IsExpired == 1)
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

    private sealed class ClassifyRow
    {
        public string? StepKey { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
        public int IsExpired { get; set; }
    }
}
