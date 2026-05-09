using System.Text;
using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// Dapper-based PostgreSQL implementation of <see cref="IWebhookRejectionStore"/>.
/// Persists DLQ + accepted-delivery rows in the <c>webhook_rejections</c> table
/// created by <see cref="PostgreSqlFlowOrchestratorMigrator"/>.
/// </summary>
public sealed class PostgreSqlWebhookRejectionStore : IWebhookRejectionStore
{
    private readonly string _connectionString;

    /// <summary>Creates the store bound to the given connection string.</summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public PostgreSqlWebhookRejectionStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Connection string required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(WebhookRejectionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO webhook_rejections
                (flow_id, trigger_key, received_at, remote_ip, reason, status_code,
                 body_bytes, body_truncated, headers_json, scheme, processing_ms, is_accepted)
            VALUES
                (@FlowId, @TriggerKey, @ReceivedAt, @RemoteIp, @Reason, @StatusCode,
                 @BodyBytes, @BodyTruncated, @HeadersJson, @Scheme, @ProcessingMs, @IsAccepted);
            """,
            new
            {
                record.FlowId,
                record.TriggerKey,
                record.ReceivedAt,
                record.RemoteIp,
                record.Reason,
                record.StatusCode,
                record.BodyBytes,
                record.BodyTruncated,
                record.HeadersJson,
                record.Scheme,
                record.ProcessingMs,
                record.IsAccepted,
            },
            cancellationToken: ct));
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<WebhookRejectionRecord>> QueryRecentAsync(
        Guid? flowId,
        string? reason,
        bool includeAccepted,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 50;
        if (take > 500) take = 500;

        var sb = new StringBuilder(
            "SELECT id AS Id, flow_id AS FlowId, trigger_key AS TriggerKey, received_at AS ReceivedAt, "
            + "remote_ip AS RemoteIp, reason AS Reason, status_code AS StatusCode, body_bytes AS BodyBytes, "
            + "body_truncated AS BodyTruncated, headers_json AS HeadersJson, scheme AS Scheme, "
            + "processing_ms AS ProcessingMs, is_accepted AS IsAccepted "
            + "FROM webhook_rejections WHERE 1=1");
        if (flowId is not null) sb.Append(" AND flow_id = @FlowId");
        if (!string.IsNullOrEmpty(reason)) sb.Append(" AND reason = @Reason");
        if (!includeAccepted) sb.Append(" AND is_accepted = FALSE");
        sb.Append(" ORDER BY id DESC OFFSET @Skip LIMIT @Take;");

        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<WebhookRejectionRecord>(new CommandDefinition(
            sb.ToString(),
            new { FlowId = flowId, Reason = reason, Skip = skip, Take = take },
            cancellationToken: ct));
        return rows.ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask<WebhookRejectionPage> QueryAsync(WebhookRejectionQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var skip = query.Skip < 0 ? 0 : query.Skip;
        var take = query.Take <= 0 ? 50 : (query.Take > 500 ? 500 : query.Take);
        var hasSearch = !string.IsNullOrEmpty(query.Search);
        var searchLike = hasSearch ? "%" + query.Search!.Replace("%", "\\%").Replace("_", "\\_") + "%" : null;

        var where = new StringBuilder("WHERE 1=1");
        if (query.FlowId is not null) where.Append(" AND flow_id = @FlowId");
        if (!string.IsNullOrEmpty(query.Reason)) where.Append(" AND reason = @Reason");
        if (!query.IncludeAccepted) where.Append(" AND is_accepted = FALSE");
        if (hasSearch) where.Append(" AND (reason ILIKE @Search OR trigger_key ILIKE @Search OR remote_ip ILIKE @Search)");

        var pagedSql = new StringBuilder()
            .Append("SELECT id AS Id, flow_id AS FlowId, trigger_key AS TriggerKey, received_at AS ReceivedAt, ")
            .Append("remote_ip AS RemoteIp, reason AS Reason, status_code AS StatusCode, body_bytes AS BodyBytes, ")
            .Append("body_truncated AS BodyTruncated, headers_json AS HeadersJson, scheme AS Scheme, ")
            .Append("processing_ms AS ProcessingMs, is_accepted AS IsAccepted ")
            .Append("FROM webhook_rejections ").Append(where)
            .Append(" ORDER BY id DESC OFFSET @Skip LIMIT @Take;").ToString();
        var totalSql = "SELECT COUNT(*)::bigint FROM webhook_rejections " + where + ";";

        await using var conn = new NpgsqlConnection(_connectionString);
        var args = new
        {
            FlowId = query.FlowId,
            Reason = query.Reason,
            Search = searchLike,
            Skip = skip,
            Take = take,
        };
        var rows = await conn.QueryAsync<WebhookRejectionRecord>(new CommandDefinition(pagedSql, args, cancellationToken: ct));
        var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(totalSql, args, cancellationToken: ct));
        return new WebhookRejectionPage(rows.ToArray(), (int)Math.Min(int.MaxValue, total));
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyDictionary<string, long>> CountsByReasonAsync(TimeSpan window, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<(string Reason, long Count)>(new CommandDefinition(
            """
            SELECT reason AS "Reason", COUNT(*)::bigint AS "Count"
            FROM webhook_rejections
            WHERE received_at >= @Cutoff AND is_accepted = FALSE AND reason <> ''
            GROUP BY reason;
            """,
            new { Cutoff = cutoff },
            cancellationToken: ct));
        return rows.ToDictionary(r => r.Reason, r => r.Count, StringComparer.OrdinalIgnoreCase);
    }
}
