using System.Buffers;
using System.Text.Json;
using Dapper;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// The built-in run-search token index for SQL Server (<c>FlowRunSearchTokens</c>), enabled by
/// <see cref="FlowOrchestratorBuilder.UseRunSearchIndex"/>. <b>Experimental.</b>
/// </summary>
/// <remarks>
/// <para>Table: <c>(Token NVARCHAR(64) BIN2, StartedAt, RunId, Source)</c>, clustered on that key with
/// <c>IGNORE_DUP_KEY</c> so concurrent steps of one run can insert the same token without an error. An
/// exact token is an ordered newest-first range and a token prefix is a range seek.</para>
/// <para>The <see cref="SqlFlowRunStore"/> writes the tokens itself, in the same round trip as the run
/// start and the step completion (as a separate statement after the step's transaction commits, so a
/// token failure or a duplicate-key wait never touches the durable write), and composes the token probe
/// into its own search batch. The <see cref="IRunSearchIndex"/> members are for host code and the
/// back-fill.</para>
/// <para>Indexed: trigger body values, step keys, error-message words and step output values (see
/// <see cref="RunSearchTokenizer"/>). The run's own columns are not tokenized: they are matched as
/// substrings through <c>RunSearchText</c>.</para>
/// </remarks>
public sealed class SqlServerRunSearchTokenIndex : IRunSearchIndex
{
    private readonly string _connectionString;

    /// <summary>Creates the index over the database at <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">The FlowOrchestrator SQL Server database.</param>
    /// <param name="options">Token caps and back-fill settings.</param>
    public SqlServerRunSearchTokenIndex(string connectionString, RunSearchIndexOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        _connectionString = connectionString;
        Options = options;
    }

    /// <summary>The options the index was created with.</summary>
    public RunSearchIndexOptions Options { get; }

    /// <inheritdoc/>
    public RunSearchCapabilities Capabilities
        => RunSearchCapabilities.TokenPrefix | RunSearchCapabilities.StepErrors | RunSearchCapabilities.Payloads | RunSearchCapabilities.OrderedByStart;

    /// <summary>
    /// The token insert, run through <c>sp_executesql</c> inside <c>TRY…CATCH</c>: compiled at run time, so
    /// a missing table or an old compatibility level (no <c>OPENJSON</c>) is a caught error instead of a
    /// compile failure of the caller's batch. Expects <c>@SearchStartedAt</c>, <c>@SearchRunId</c> and
    /// <c>@SearchTokens</c> to be declared or bound by the caller.
    /// </summary>
    internal const string InsertTokensSql = """
        IF @SearchTokens IS NOT NULL AND @SearchStartedAt IS NOT NULL
        BEGIN
            BEGIN TRY
                EXEC sp_executesql
                    N'INSERT INTO FlowRunSearchTokens (Token, StartedAt, RunId, Source)
                      SELECT j.t, @a, @r, j.s FROM OPENJSON(@j) WITH (t NVARCHAR(64) ''$.t'', s TINYINT ''$.s'') AS j
                      ORDER BY j.t, j.s',
                    N'@a DATETIMEOFFSET, @r UNIQUEIDENTIFIER, @j NVARCHAR(MAX)',
                    @a = @SearchStartedAt, @r = @SearchRunId, @j = @SearchTokens;
            END TRY
            BEGIN CATCH
                SET @SearchTokens = NULL;   -- best effort: the durable write above already committed
            END CATCH
        END
        """;

    /// <summary>Tokenizes a run start: trigger body values (the run columns are matched through <c>RunSearchText</c>).</summary>
    /// <param name="triggerDataJson">The trigger body.</param>
    /// <returns>The token JSON for <see cref="InsertTokensSql"/>, or <see langword="null"/> when there is nothing to index.</returns>
    internal string? TokenizeRun(string? triggerDataJson)
    {
        if (string.IsNullOrEmpty(triggerDataJson))
            return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        RunSearchTokenizer.AddJsonValues(triggerDataJson, set, MaxTokens);
        return set.Count == 0 ? null : ToJson(set, RunSearchTokenSource.Trigger, null, default, null, default);
    }

    /// <summary>Tokenizes one step completion: step key, error-message words, output values.</summary>
    /// <param name="stepKey">The step key.</param>
    /// <param name="errorMessage">The failure reason.</param>
    /// <param name="outputJson">The step output.</param>
    /// <returns>The token JSON for <see cref="InsertTokensSql"/>, or <see langword="null"/> when there is nothing to index.</returns>
    /// <remarks>Cost: O(length of the step's key, error and output); never depends on the number of steps in the run.</remarks>
    internal string? TokenizeStep(string stepKey, string? errorMessage, string? outputJson)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        RunSearchTokenizer.AddText(stepKey, keys, camelCase: true, MaxTokens);
        HashSet<string>? errors = null;
        if (!string.IsNullOrEmpty(errorMessage))
        {
            errors = new HashSet<string>(StringComparer.Ordinal);
            RunSearchTokenizer.AddText(errorMessage, errors, camelCase: false, MaxTokens);
        }

        HashSet<string>? payload = null;
        if (!string.IsNullOrEmpty(outputJson))
        {
            payload = new HashSet<string>(StringComparer.Ordinal);
            RunSearchTokenizer.AddJsonValues(outputJson, payload, MaxTokens);
        }

        return keys.Count + (errors?.Count ?? 0) + (payload?.Count ?? 0) == 0
            ? null
            : ToJson(keys, RunSearchTokenSource.StepKey, errors, RunSearchTokenSource.Error, payload, RunSearchTokenSource.Payload);
    }

    private int MaxTokens => Options.MaxTokensPerDocument > 0 ? Options.MaxTokensPerDocument : RunSearchTokenizer.DefaultMaxTokensPerDocument;

    private static string ToJson(
        HashSet<string> first, RunSearchTokenSource firstSource,
        HashSet<string>? second, RunSearchTokenSource secondSource,
        HashSet<string>? third, RunSearchTokenSource thirdSource)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            Write(writer, first, firstSource);
            if (second is not null) Write(writer, second, secondSource);
            if (third is not null) Write(writer, third, thirdSource);
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void Write(Utf8JsonWriter writer, HashSet<string> tokens, RunSearchTokenSource source)
        {
            foreach (var token in tokens)
            {
                writer.WriteStartObject();
                writer.WriteString("t"u8, token);
                writer.WriteNumber("s"u8, (byte)source);
                writer.WriteEndObject();
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask IndexRunAsync(RunSearchDocument run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await InsertAsync(run.RunId, run.StartedAt, TokenizeRun(run.TriggerDataJson), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask IndexStepAsync(StepSearchDocument step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        var tokens = TokenizeStep(step.StepKey, step.ErrorMessage, step.OutputJson);
        if (tokens is null)
            return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition(
            "DECLARE @SearchStartedAt DATETIMEOFFSET = (SELECT StartedAt FROM FlowRuns WHERE Id = @SearchRunId);\n" + InsertTokensSql,
            new { SearchRunId = step.RunId, SearchTokens = tokens },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task InsertAsync(Guid runId, DateTimeOffset startedAt, string? tokens, CancellationToken cancellationToken)
    {
        if (tokens is null)
            return;
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition(
            "DECLARE @SearchStartedAt DATETIMEOFFSET = @StartedAt;\n" + InsertTokensSql,
            new { StartedAt = startedAt, SearchRunId = runId, SearchTokens = tokens },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every query token must be a prefix of one of the run's tokens. Returns at most
    /// <see cref="RunSearchIndexQuery.Limit"/> runs, newest first. The run's own columns are not in the
    /// token table; the store matches them separately.
    /// </remarks>
    public async Task<RunSearchIndexResult> SearchAsync(RunSearchIndexQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var tokens = RunSearchTokenizer.QueryTokens(query.Text, SqlRunSearchCommandBuilder.MaxQueryTokens);
        if (tokens.Count == 0 || query.Limit <= 0)
            return new RunSearchIndexResult([], false);

        var (sql, parameters) = BuildSearch(tokens, query);
        await using var conn = new SqlConnection(_connectionString);
        var rows = (await conn.QueryAsync<(Guid RunId, DateTimeOffset StartedAt)>(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
        var truncated = rows.Count > query.Limit;
        var hits = new List<RunSearchHit>(Math.Min(rows.Count, query.Limit));
        for (var i = 0; i < rows.Count && i < query.Limit; i++)
            hits.Add(new RunSearchHit(rows[i].RunId, rows[i].StartedAt));
        return new RunSearchIndexResult(hits, truncated);
    }

    /// <summary>Builds the standalone token probe used by <see cref="SearchAsync"/>.</summary>
    internal static (string Sql, DynamicParameters Parameters) BuildSearch(IReadOnlyList<string> tokens, RunSearchIndexQuery query)
    {
        var p = new DynamicParameters();
        p.Add("LimitPlusOne", (int)Math.Min((long)query.Limit + 1, int.MaxValue));
        var window = string.Empty;
        if (query.StartedFrom.HasValue) { window += " AND t.StartedAt >= @StartedFrom"; p.Add("StartedFrom", query.StartedFrom.Value.ToUniversalTime()); }
        if (query.StartedTo.HasValue) { window += " AND t.StartedAt <= @StartedTo"; p.Add("StartedTo", query.StartedTo.Value.ToUniversalTime()); }

        var sql = new System.Text.StringBuilder("SELECT TOP (@LimitPlusOne) t.RunId, MAX(t.StartedAt) AS StartedAt FROM FlowRunSearchTokens AS t WHERE t.Token LIKE @Tok0 ESCAPE '\\'");
        p.Add("Tok0", SqlRunSearchCommandBuilder.EscapeLike(tokens[0]) + "%");
        sql.Append(window);
        for (var i = 1; i < tokens.Count; i++)
        {
            var name = "Tok" + i;
            p.Add(name, SqlRunSearchCommandBuilder.EscapeLike(tokens[i]) + "%");
            sql.Append(" AND t.RunId IN (SELECT u.RunId FROM FlowRunSearchTokens AS u WHERE u.Token LIKE @").Append(name).Append(" ESCAPE '\\'")
               .Append(window.Replace("t.StartedAt", "u.StartedAt", StringComparison.Ordinal)).Append(')');
        }

        sql.Append(" GROUP BY t.RunId ORDER BY MAX(t.StartedAt) DESC;");
        return (sql.ToString(), p);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The token table has no index on <c>RunId</c> (it would double its size), so this does not delete by
    /// id: it sweeps tokens whose run no longer exists, in one keyset pass over the clustered key. The
    /// back-fill service runs the same sweep every <see cref="RunSearchIndexOptions.OrphanSweepInterval"/>.
    /// </remarks>
    public Task RemoveRunsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken)
        => SweepOrphansAsync(cancellationToken);

    /// <summary>Deletes tokens of runs that no longer exist, in batches along the clustered key.</summary>
    /// <param name="cancellationToken">Stops between batches.</param>
    /// <returns>The number of token rows deleted.</returns>
    /// <remarks>Cost: one ordered scan of the token table plus a primary-key probe of <c>FlowRuns</c> per distinct run.</remarks>
    public async Task<long> SweepOrphansAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @deleted TABLE (Token NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL);
            WITH batch AS (
                SELECT TOP (@Batch) t.Token, t.StartedAt, t.RunId, t.Source
                FROM FlowRunSearchTokens AS t
                WHERE t.Token >= @From
                  AND NOT EXISTS (SELECT 1 FROM FlowRuns AS fr WHERE fr.Id = t.RunId)
                ORDER BY t.Token, t.StartedAt DESC, t.RunId, t.Source
            )
            DELETE FROM batch OUTPUT deleted.Token INTO @deleted;
            SELECT COUNT(*) AS Deleted, MAX(Token) AS LastToken FROM @deleted;
            """;

        long total = 0;
        var from = string.Empty;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var (deleted, last) = await conn.QuerySingleAsync<(int Deleted, string? LastToken)>(new CommandDefinition(
                sql, new { Batch = 5_000, From = from }, commandTimeout: 300, cancellationToken: cancellationToken)).ConfigureAwait(false);
            total += deleted;
            if (deleted == 0 || last is null)
                break;
            from = last;   // inclusive: other runs' rows with the same token are re-checked, never skipped
        }

        return total;
    }
}
