using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Dapper-based SQL Server implementation of <see cref="IWebhookReplayStore"/>.
/// Persists nonces in the <c>WebhookReplayNonces</c> table created by
/// <see cref="FlowOrchestratorSqlMigrator"/>.
/// </summary>
public sealed class SqlWebhookReplayStore : IWebhookReplayStore
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
    public async ValueTask<bool> TryRegisterAsync(
        Guid flowId,
        string triggerKey,
        string nonce,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        // INSERT … WHERE NOT EXISTS — primary key collision returns 0 rows on replay.
        // The PK on (FlowId, TriggerKey, Nonce) makes the insert atomic so two
        // concurrent inserters race correctly: at most one wins.
        await using var conn = new SqlConnection(_connectionString);
        var inserted = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO [WebhookReplayNonces] ([FlowId], [TriggerKey], [Nonce], [ExpiresAt])
            SELECT @FlowId, @TriggerKey, @Nonce, @ExpiresAt
            WHERE NOT EXISTS (
                SELECT 1 FROM [WebhookReplayNonces]
                WHERE [FlowId] = @FlowId AND [TriggerKey] = @TriggerKey AND [Nonce] = @Nonce
            );
            """,
            new
            {
                FlowId = flowId,
                TriggerKey = triggerKey,
                Nonce = nonce,
                ExpiresAt = expiresAt,
            },
            cancellationToken: ct));
        return inserted == 1;
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
