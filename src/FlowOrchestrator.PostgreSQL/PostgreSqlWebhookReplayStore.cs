using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// Dapper-based PostgreSQL implementation of <see cref="IWebhookReplayStore"/>.
/// Persists nonces in the <c>webhook_replay_nonces</c> table created by
/// <see cref="PostgreSqlFlowOrchestratorMigrator"/>.
/// </summary>
public sealed class PostgreSqlWebhookReplayStore : IWebhookReplayStore
{
    private readonly string _connectionString;

    /// <summary>Creates the store bound to the given connection string.</summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public PostgreSqlWebhookReplayStore(string connectionString)
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
        // ON CONFLICT DO NOTHING + RETURNING — postgres reports the inserted row
        // count via the rows-affected channel; conflict path returns 0.
        await using var conn = new NpgsqlConnection(_connectionString);
        var inserted = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO webhook_replay_nonces (flow_id, trigger_key, nonce, expires_at)
            VALUES (@FlowId, @TriggerKey, @Nonce, @ExpiresAt)
            ON CONFLICT (flow_id, trigger_key, nonce) DO NOTHING;
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
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM webhook_replay_nonces WHERE expires_at <= @Now;",
            new { Now = now },
            cancellationToken: ct));
    }
}
