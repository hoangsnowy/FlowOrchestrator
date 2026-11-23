using Dapper;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Which run-search structures exist in the database right now. The migrator adds them additively and
/// may build the indexes in the background, so the store builds its queries from this snapshot and
/// falls back to shapes that need none of them while they are missing.
/// </summary>
/// <param name="SearchColumn"><c>FlowRuns.RunSearchText</c> (computed, lower-cased, BIN2) exists.</param>
/// <param name="SearchIndex"><c>IX_FlowRuns_StartedAt_Search</c> exists, so it can be hinted.</param>
/// <param name="IdTextIndex"><c>IX_FlowRuns_IdText</c> exists, so an id prefix is a seek at any age.</param>
/// <param name="TokenTable">The token table exists and the database can run the token statements (compatibility level 130+).</param>
/// <param name="UnindexedAtOrBefore">
/// While the token back-fill is incomplete: runs that started at or before this instant may be missing
/// from the token table, so deep search keeps the <c>LIKE</c> probe for them. <see langword="null"/> once
/// the back-fill has completed.
/// </param>
internal sealed record SqlRunSearchSchema(
    bool SearchColumn,
    bool SearchIndex,
    bool IdTextIndex,
    bool TokenTable,
    DateTimeOffset? UnindexedAtOrBefore)
{
    /// <summary>A database without any of the 2.0 search structures (pre-migration, or migration failed).</summary>
    public static readonly SqlRunSearchSchema None = new(false, false, false, false, null);

    /// <summary>Every quick-search structure present; no token table.</summary>
    public static readonly SqlRunSearchSchema Complete = new(true, true, true, false, null);

    /// <summary>Whether nothing about this snapshot can change any more, so it need not be re-probed.</summary>
    /// <param name="tokensEnabled">Whether the token index is enabled for this store.</param>
    public bool IsFinal(bool tokensEnabled)
        => SearchColumn && SearchIndex && IdTextIndex && (!tokensEnabled || (TokenTable && UnindexedAtOrBefore is null));

    /// <summary>One round trip that reads every flag; requires only metadata visibility on <c>FlowRuns</c>.</summary>
    internal const string ProbeSql = """
        SELECT
            CAST(CASE WHEN COL_LENGTH(N'FlowRuns', N'RunSearchText') IS NULL THEN 0 ELSE 1 END AS BIT) AS SearchColumn,
            CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowRuns_StartedAt_Search' AND object_id = OBJECT_ID(N'FlowRuns')) THEN 1 ELSE 0 END AS BIT) AS SearchIndex,
            CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowRuns_IdText' AND object_id = OBJECT_ID(N'FlowRuns')) THEN 1 ELSE 0 END AS BIT) AS IdTextIndex,
            CAST(CASE WHEN OBJECT_ID(N'FlowRunSearchTokens') IS NOT NULL
                       AND OBJECT_ID(N'FlowRunSearchIndexState') IS NOT NULL
                       AND (SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID()) >= 130
                      THEN 1 ELSE 0 END AS BIT) AS TokenTable;
        IF OBJECT_ID(N'FlowRunSearchIndexState') IS NOT NULL
            EXEC(N'SELECT EnabledAt, BackfillAfterStartedAt, BackfillCompletedAt FROM FlowRunSearchIndexState WHERE Id = 1');
        """;
}

/// <summary>
/// Caches the <see cref="SqlRunSearchSchema"/> snapshot for one store. Re-probes at most once per
/// <see cref="RefreshInterval"/> until the snapshot is final, then never again.
/// </summary>
/// <remarks>
/// Cost on the search path: zero once final; otherwise one metadata query per interval. Never on the
/// step write path — writes read only the last snapshot.
/// </remarks>
internal sealed class SqlRunSearchSchemaCache
{
    /// <summary>How long a non-final snapshot is trusted before the next probe.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly string _connectionString;
    private readonly bool _tokensEnabled;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private SqlRunSearchSchema? _snapshot;
    private long _probedAtTicks;

    public SqlRunSearchSchemaCache(string connectionString, bool tokensEnabled, TimeProvider? time = null)
    {
        _connectionString = connectionString;
        _tokensEnabled = tokensEnabled;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The last snapshot without probing; <see cref="SqlRunSearchSchema.None"/> before the first probe.</summary>
    public SqlRunSearchSchema Current => Volatile.Read(ref _snapshot) ?? SqlRunSearchSchema.None;

    /// <summary>Returns a snapshot no older than <see cref="RefreshInterval"/> unless it is final.</summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    public async ValueTask<SqlRunSearchSchema> GetAsync(CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var now = _time.GetUtcNow().UtcTicks;
        if (snapshot is not null
            && (snapshot.IsFinal(_tokensEnabled) || now - Interlocked.Read(ref _probedAtTicks) < RefreshInterval.Ticks))
        {
            return snapshot;
        }

        var probed = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _snapshot = probed;
            Interlocked.Exchange(ref _probedAtTicks, now);
        }

        return probed;
    }

    /// <summary>Forgets the snapshot, e.g. after a statement failed because a structure it relied on vanished.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshot = null;
        }
    }

    private async Task<SqlRunSearchSchema> ProbeAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        using var grid = await conn.QueryMultipleAsync(new CommandDefinition(
            SqlRunSearchSchema.ProbeSql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var flags = await grid.ReadSingleAsync<(bool SearchColumn, bool SearchIndex, bool IdTextIndex, bool TokenTable)>().ConfigureAwait(false);
        DateTimeOffset? unindexed = null;
        if (!grid.IsConsumed)
        {
            var state = await grid.ReadSingleOrDefaultAsync<(DateTimeOffset EnabledAt, DateTimeOffset? BackfillAfterStartedAt, DateTimeOffset? BackfillCompletedAt)>().ConfigureAwait(false);
            if (state.BackfillCompletedAt is null && state.EnabledAt != default)
                unindexed = state.BackfillAfterStartedAt ?? state.EnabledAt;
            else if (state.EnabledAt == default)
                unindexed = DateTimeOffset.MaxValue;   // table exists but no state row yet: nothing is known to be indexed
        }

        var tokenTable = _tokensEnabled && flags.TokenTable;
        return new SqlRunSearchSchema(flags.SearchColumn, flags.SearchIndex, flags.IdTextIndex, tokenTable, tokenTable ? unindexed : null);
    }
}
