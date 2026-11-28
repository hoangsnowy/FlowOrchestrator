using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Dapper-based SQL Server implementation of <see cref="IWebhookReplayStore"/>.
/// Persists nonces in the <c>WebhookReplayNonces</c> table created by
/// <see cref="FlowOrchestratorSqlMigrator"/>.
/// </summary>
internal sealed class SqlWebhookReplayStore : IWebhookReplayStore
{
    private readonly string _connectionString;

    /// <summary>Creates the store bound to the given connection string.</summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    public SqlWebhookReplayStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Connection string required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    /// <inheritdoc/>
    public ValueTask<bool> TryRegisterAsync(
        Guid flowId,
        string triggerKey,
        string nonce,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
        => TryRegisterAsync(flowId, triggerKey, nonce, expiresAt, Guid.NewGuid(), ct);

    /// <inheritdoc/>
    public async ValueTask<bool> TryRegisterAsync(
        Guid flowId,
        string triggerKey,
        string nonce,
        DateTimeOffset expiresAt,
        Guid registrationId,
        CancellationToken ct = default)
    {
        // INSERT … WHERE NOT EXISTS returns 0 rows on a replay. Without a lock on the probe two
        // concurrent deliveries of one nonce both pass NOT EXISTS and the loser throws a PK
        // violation (a 500 to the webhook caller instead of the replay verdict). UPDLOCK, HOLDLOCK
        // makes the second inserter block until the first commits and then see the row; a
        // duplicate-key error that still slips through is reported as "already seen".
        await using var conn = new SqlConnection(_connectionString);
        try
        {
            var inserted = await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO [WebhookReplayNonces] ([FlowId], [TriggerKey], [Nonce], [ExpiresAt], [RegistrationId])
                SELECT @FlowId, @TriggerKey, @Nonce, @ExpiresAt, @RegistrationId
                WHERE NOT EXISTS (
                    SELECT 1 FROM [WebhookReplayNonces] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [FlowId] = @FlowId AND [TriggerKey] = @TriggerKey AND [Nonce] = @Nonce
                );
                """,
                new
                {
                    FlowId = flowId,
                    TriggerKey = triggerKey,
                    // The column is 256 characters: an over-long nonce is stored as its digest (no 2628 / 22001).
                    Nonce = WebhookReplayNonceStorage.ToStorageForm(nonce),
                    ExpiresAt = expiresAt,
                    RegistrationId = registrationId,
                },
                cancellationToken: ct));
            return inserted == 1;
        }
        catch (SqlException ex) when (SqlUniqueViolation.Matches(ex))
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryForgetAsync(
        Guid flowId,
        string triggerKey,
        string nonce,
        Guid registrationId,
        CancellationToken ct = default)
    {
        if (registrationId == Guid.Empty) return false;

        // Point delete on the primary key, guarded by the owner token: a row registered by another request
        // (or before the owner column existed, NULL) never matches.
        await using var conn = new SqlConnection(_connectionString);
        var deleted = await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM [WebhookReplayNonces]
            WHERE [FlowId] = @FlowId AND [TriggerKey] = @TriggerKey AND [Nonce] = @Nonce
              AND [RegistrationId] = @RegistrationId;
            """,
            new
            {
                FlowId = flowId,
                TriggerKey = triggerKey,
                Nonce = WebhookReplayNonceStorage.ToStorageForm(nonce),
                RegistrationId = registrationId,
            },
            cancellationToken: ct));
        return deleted == 1;
    }

    /// <inheritdoc/>
    public async ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM [WebhookReplayNonces] WHERE [ExpiresAt] <= @Now;",
            new { Now = now },
            cancellationToken: ct));
    }
}
