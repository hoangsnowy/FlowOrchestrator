using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// Pins the SQL Server run-search batch shapes measured in <c>docs/benchmarks/run-search-2026-11-23.md</c>:
/// BIN2 predicates, the narrow search column, the id prefix as its own statement, keyset paging, the
/// capped count, and the token-table probe with its back-fill fallback. Pure text checks — no database.
/// </summary>
public sealed class SqlRunSearchCommandBuilderTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly SqlRunSearchSchema Tokens = SqlRunSearchSchema.Complete with { TokenTable = true };

    private static SqlRunSearchCommand Build(RunSearchQuery query, SqlRunSearchSchema? schema = null, bool external = false)
        => SqlRunSearchCommandBuilder.Build(query, schema ?? SqlRunSearchSchema.Complete, external);

    private static T? Param<T>(SqlRunSearchCommand command, string name) => command.Parameters.Get<T>(name);

    [Fact]
    public void Listing_orders_by_start_time_then_id_and_reads_take_plus_one()
    {
        // Arrange
        var query = new RunSearchQuery { Take = 20, Skip = 40 };

        // Act
        var command = Build(query);

        // Assert
        Assert.Contains("ORDER BY fr.StartedAt DESC, fr.Id OFFSET @Skip ROWS FETCH NEXT @TakePlusOne ROWS ONLY", command.Sql);
        Assert.Equal(21, Param<int>(command, "TakePlusOne"));
        Assert.Equal(40, Param<int>(command, "Skip"));
        Assert.False(command.Merge);
        Assert.Equal(1, command.RowSets);
    }

    [Fact]
    public void Cursor_becomes_a_keyset_predicate_and_is_not_part_of_the_count()
    {
        // Arrange
        var after = new RunSearchCursor(From, Guid.NewGuid());
        var query = new RunSearchQuery { After = after, IncludeTotal = true };

        // Act
        var command = Build(query);

        // Assert
        Assert.Contains("(fr.StartedAt < @AfterStartedAt OR (fr.StartedAt = @AfterStartedAt AND fr.Id > @AfterId))", command.Sql);
        Assert.Equal(after.Id, Param<Guid>(command, "AfterId"));
        var count = command.Sql[command.Sql.IndexOf("SELECT COUNT(*)", StringComparison.Ordinal)..];
        Assert.DoesNotContain("@AfterId", count);
    }

    [Fact]
    public void Capped_count_stops_after_cap_plus_one_rows()
    {
        // Arrange
        var query = new RunSearchQuery { IncludeTotal = true, TotalCountCap = 10_000, Status = "Failed" };

        // Act
        var command = Build(query);

        // Assert
        Assert.True(command.CountsTotal);
        Assert.Contains("SELECT COUNT(*) FROM (SELECT TOP (@CountLimit) 1 AS x FROM FlowRuns AS fr WHERE fr.Status = @Status) AS capped;", command.Sql);
        Assert.Equal(10_001, Param<int>(command, "CountLimit"));
    }

    [Fact]
    public void Exact_count_when_no_cap_is_set()
    {
        // Arrange
        var query = new RunSearchQuery { IncludeTotal = true };

        // Act
        var command = Build(query);

        // Assert
        Assert.Contains(" SELECT COUNT(*) FROM FlowRuns AS fr;", command.Sql);
        Assert.DoesNotContain("@CountLimit", command.Sql);
    }

    [Fact]
    public void Quick_search_reads_the_narrow_column_through_its_index_with_a_lower_cased_term()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "  Order_100% ", StartedFrom = From };

        // Act
        var command = Build(query);

        // Assert
        Assert.Contains("WITH (INDEX(IX_FlowRuns_StartedAt_Search))", command.Sql);
        Assert.Contains("AS m INNER JOIN FlowRuns AS fr ON fr.Id = m.Id ORDER BY m.StartedAt DESC, m.Id;", command.Sql);   // no sort of the wide rows
        Assert.Contains("fr.RunSearchText LIKE @LowerLike ESCAPE '\\'", command.Sql);
        Assert.Contains("fr.StartedAt >= @StartedFrom", command.Sql);
        Assert.Equal("%order\\_100\\%%", Param<string>(command, "LowerLike"));
        Assert.DoesNotContain("FlowSteps", command.Sql);
        Assert.DoesNotContain("ISNULL(", command.Sql);
        Assert.False(command.CountsTotal);
    }

    [Fact]
    public void Term_containing_the_column_separator_matches_column_by_column()
    {
        // Arrange — RunSearchText joins the columns with '|', which must not let a term span two columns.
        var query = new RunSearchQuery { Text = "Flow|manual" };

        // Act
        var command = Build(query);

        // Assert
        Assert.DoesNotContain("RunSearchText", command.Sql);
        Assert.Contains("LOWER(fr.FlowName) COLLATE Latin1_General_100_BIN2 LIKE @LowerLike", command.Sql);
    }

    [Fact]
    public void Database_without_the_search_structures_gets_the_bin2_column_shape_and_no_hints()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "bbdafe3e", StartedFrom = From };

        // Act
        var command = Build(query, SqlRunSearchSchema.None);

        // Assert
        Assert.DoesNotContain("RunSearchText", command.Sql);
        Assert.DoesNotContain("IdText", command.Sql);
        Assert.DoesNotContain("WITH (INDEX(", command.Sql);
        Assert.Contains("LOWER(CONVERT(NVARCHAR(36), fr.Id)) COLLATE Latin1_General_100_BIN2 LIKE @LowerLike", command.Sql);
        Assert.False(command.Merge);
    }

    [Fact]
    public void Hex_prefix_is_its_own_statement_before_the_text_search_and_ignores_the_window()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "bbdafe3e", StartedFrom = From, Take = 50, Skip = 0 };

        // Act
        var command = Build(query);

        // Assert
        var idStatement = command.Sql.IndexOf("fr.IdText LIKE @IdPrefix", StringComparison.Ordinal);
        var textStatement = command.Sql.IndexOf("RunSearchText LIKE", StringComparison.Ordinal);
        Assert.InRange(idStatement, 0, textStatement);
        var firstStatement = command.Sql[..command.Sql.IndexOf(';', StringComparison.Ordinal)];
        Assert.DoesNotContain("@StartedFrom", firstStatement);
        Assert.DoesNotContain("UNION", command.Sql);
        Assert.Equal("BBDAFE3E%", Param<string>(command, "IdPrefix"));
        Assert.True(command.Merge);
        Assert.Equal(2, command.RowSets);
        Assert.Equal(51, Param<int>(command, "MergeTake"));
    }

    [Fact]
    public void Full_guid_text_is_a_primary_key_statement()
    {
        // Arrange
        var id = Guid.NewGuid();
        var query = new RunSearchQuery { Text = id.ToString("D").ToUpperInvariant() };

        // Act
        var command = Build(query);

        // Assert
        Assert.Contains("fr.Id = @TextId", command.Sql);
        Assert.DoesNotContain("IdText", command.Sql);
        Assert.Equal(id, Param<Guid>(command, "TextId"));
        Assert.True(command.Merge);
    }

    [Fact]
    public void Merged_statements_skip_after_the_merge()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "bbdafe3e", Take = 10, Skip = 30 };

        // Act
        var command = Build(query);

        // Assert
        Assert.DoesNotContain("OFFSET", command.Sql);
        Assert.Equal(41, Param<int>(command, "MergeTake"));
    }

    [Fact]
    public void Deep_search_without_the_token_index_probes_steps_with_bin2_predicates()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "INV-001353358", IncludeStepPayloads = true, StartedFrom = From };

        // Act
        var command = Build(query);

        // Assert
        Assert.Contains("LOWER(fs.OutputJson) COLLATE Latin1_General_100_BIN2 LIKE @LowerLike ESCAPE '\\'", command.Sql);
        Assert.Contains("LOWER(fs.ErrorMessage) COLLATE Latin1_General_100_BIN2 LIKE @LowerLike", command.Sql);
        Assert.Contains("LOWER(fs.StepKey) COLLATE Latin1_General_100_BIN2 LIKE @LowerLike", command.Sql);
        Assert.DoesNotContain("FlowRunSearchTokens", command.Sql);
        Assert.Equal("%inv-001353358%", Param<string>(command, "LowerLike"));
    }

    [Fact]
    public void Deep_search_with_a_complete_token_index_does_not_read_step_payloads()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "deadlock victim", IncludeStepPayloads = true, StartedFrom = From };

        // Act
        var command = Build(query, Tokens);

        // Assert
        Assert.DoesNotContain("FlowSteps", command.Sql);
        Assert.Contains("FlowRunSearchTokens AS t WHERE t.Token LIKE @Tok0 ESCAPE '\\' AND t.StartedAt >= @StartedFrom", command.Sql);
        Assert.Contains("t.Token LIKE @Tok1", command.Sql);
        Assert.Equal("deadlock%", Param<string>(command, "Tok0"));
        Assert.Equal("victim%", Param<string>(command, "Tok1"));
        Assert.True(command.Merge);
    }

    [Fact]
    public void Deep_search_keeps_the_like_probe_for_runs_the_backfill_has_not_reached()
    {
        // Arrange
        var watermark = From.AddDays(-30);
        var query = new RunSearchQuery { Text = "INV-1", IncludeStepPayloads = true };

        // Act
        var command = Build(query, Tokens with { UnindexedAtOrBefore = watermark });

        // Assert
        Assert.Contains("fr.StartedAt <= @UnindexedAtOrBefore AND EXISTS (SELECT 1 FROM FlowSteps", command.Sql);
        Assert.Contains("FlowRunSearchTokens", command.Sql);
        Assert.Equal(watermark, Param<DateTimeOffset>(command, "UnindexedAtOrBefore"));
    }

    [Fact]
    public void Quick_search_never_touches_the_token_table()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "order-created" };

        // Act
        var command = Build(query, Tokens);

        // Assert
        Assert.DoesNotContain("FlowRunSearchTokens", command.Sql);
    }

    [Fact]
    public void External_index_replaces_the_payload_probe()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "INV-1", IncludeStepPayloads = true };

        // Act
        var command = Build(query, external: true);

        // Assert
        Assert.Equal("INV-1", command.ExternalIndexText);
        Assert.DoesNotContain("FlowSteps", command.Sql);
        Assert.True(command.Merge);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("[x]", "\\[x]")]
    [InlineData("c:\\temp", "c:\\\\temp")]
    public void EscapeLike_makes_wildcards_literal(string value, string expected)
    {
        // Arrange

        // Act
        var escaped = SqlRunSearchCommandBuilder.EscapeLike(value);

        // Assert
        Assert.Equal(expected, escaped);
    }

    [Fact]
    public void Merge_dedupes_orders_by_start_then_sql_server_guid_order_and_applies_the_offset()
    {
        // Arrange — SQL Server orders uniqueidentifier by its last six bytes first.
        var at = From;
        var lowInSql = new FlowRunRecord { Id = new Guid("ffffffff-ffff-ffff-ffff-000000000001"), StartedAt = at };
        var highInSql = new FlowRunRecord { Id = new Guid("00000000-0000-0000-0000-000000000002"), StartedAt = at };
        var newest = new FlowRunRecord { Id = Guid.NewGuid(), StartedAt = at.AddMinutes(1) };
        var oldest = new FlowRunRecord { Id = Guid.NewGuid(), StartedAt = at.AddMinutes(-1) };
        IReadOnlyList<FlowRunRecord> idRows = [highInSql, newest];
        IReadOnlyList<FlowRunRecord> textRows = [newest, lowInSql, oldest];

        // Act
        var all = SqlRunSearchCommandBuilder.Merge([idRows, textRows], skip: 0, take: 10);
        var page = SqlRunSearchCommandBuilder.Merge([idRows, textRows], skip: 1, take: 1);

        // Assert
        Assert.Equal(new[] { newest.Id, lowInSql.Id, highInSql.Id, oldest.Id }, all.Select(r => r.Id));
        Assert.Equal(new[] { lowInSql.Id, highInSql.Id }, page.Select(r => r.Id));   // take + 1 rows
    }
}
