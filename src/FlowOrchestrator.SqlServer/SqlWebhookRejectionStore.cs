using System.Text;
using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Dapper-based SQL Server implementation of <see cref="IWebhookRejectionStore"/>.
/// Persists DLQ + accepted-delivery rows in the <c>WebhookRejections</c> table
/// created by <see cref="FlowOrchestratorSqlMigrator"/>.
/// </summary>
public sealed class SqlWebhookRejectionStore : IWebhookRejectionStore
{
    private readonly string _connectionString;

    /// <summary>Creates the store bound to the given connection string.</summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    public SqlWebhookRejectionStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Connection string required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(WebhookRejectionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO [WebhookRejections]
                ([FlowId], [TriggerKey], [ReceivedAt], [RemoteIp], [Reason], [StatusCode],
                 [BodyBytes], [BodyTruncated], [HeadersJson], [Scheme], [ProcessingMs], [IsAccepted])
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
            "SELECT [Id], [FlowId], [TriggerKey], [ReceivedAt], [RemoteIp], [Reason], [StatusCode], [BodyBytes], [BodyTruncated], [HeadersJson], [Scheme], [ProcessingMs], [IsAccepted] FROM [WebhookRejections] WHERE 1=1");
        if (flowId is not null) sb.Append(" AND [FlowId] = @FlowId");
        if (!string.IsNullOrEmpty(reason)) sb.Append(" AND [Reason] = @Reason");
        if (!includeAccepted) sb.Append(" AND [IsAccepted] = 0");
        sb.Append(" ORDER BY [Id] DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;");

        await using var conn = new SqlConnection(_connectionString);
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
        var searchLike = hasSearch ? "%" + query.Search!.Replace("%", "[%]").Replace("_", "[_]") + "%" : null;

        var where = new StringBuilder("WHERE 1=1");
        if (query.FlowId is not null) where.Append(" AND [FlowId] = @FlowId");
        if (!string.IsNullOrEmpty(query.Reason)) where.Append(" AND [Reason] = @Reason");
        if (!query.IncludeAccepted) where.Append(" AND [IsAccepted] = 0");
        if (!query.IncludeRejected) where.Append(" AND [IsAccepted] = 1");
        if (hasSearch) where.Append(" AND ([Reason] LIKE @Search OR [TriggerKey] LIKE @Search OR [RemoteIp] LIKE @Search)");

        var pagedSql = new StringBuilder()
            .Append("SELECT [Id], [FlowId], [TriggerKey], [ReceivedAt], [RemoteIp], [Reason], [StatusCode], ")
            .Append("[BodyBytes], [BodyTruncated], [HeadersJson], [Scheme], [ProcessingMs], [IsAccepted] ")
            .Append("FROM [WebhookRejections] ").Append(where)
            .Append(" ORDER BY [Id] DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;").ToString();
        var totalSql = "SELECT COUNT_BIG(*) FROM [WebhookRejections] " + where + ";";

        await using var conn = new SqlConnection(_connectionString);
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
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<(string Reason, long Count)>(new CommandDefinition(
            """
            SELECT [Reason], COUNT_BIG(*) AS [Count]
            FROM [WebhookRejections]
            WHERE [ReceivedAt] >= @Cutoff AND [IsAccepted] = 0 AND [Reason] <> ''
            GROUP BY [Reason];
            """,
            new { Cutoff = cutoff },
            cancellationToken: ct));
        return rows.ToDictionary(r => r.Reason, r => r.Count, StringComparer.OrdinalIgnoreCase);
    }
}
