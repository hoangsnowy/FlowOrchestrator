using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Pins the PostgreSQL run-search batch shapes measured in <c>docs/benchmarks/run-search-2026-11-23.md</c>:
/// the trigram-indexed run search expression, the run-id prefix as its own primary-key range statement,
/// deep search as a correlated <c>EXISTS</c>, keyset paging and the capped count. Pure text checks.
/// </summary>
public sealed class PostgreSqlRunSearchCommandBuilderTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private static T? Param<T>(PostgreSqlRunSearchCommand command, string name) => command.Parameters.Get<T>(name);

    [Fact]
    public void Listing_orders_by_start_time_then_id_with_a_capped_count()
    {
        // Arrange
        var query = new RunSearchQuery { Take = 20, IncludeTotal = true, TotalCountCap = 10_000, Status = "Failed" };

        // Act
        var command = PostgreSqlRunSearchCommandBuilder.Build(query);

        // Assert
        Assert.Contains("ORDER BY fr.started_at DESC, fr.id LIMIT @TakePlusOne OFFSET @Skip;", command.Sql);
        Assert.Contains("SELECT COUNT(*)::int FROM (SELECT 1 FROM flow_runs AS fr WHERE fr.status = @Status LIMIT @CountLimit) AS capped;", command.Sql);
        Assert.Equal(10_001, Param<int>(command, "CountLimit"));
        Assert.True(command.CountsTotal);
        Assert.False(command.Merge);
    }

    [Fact]
    public void Cursor_is_a_keyset_predicate_outside_the_count()
    {
        // Arrange
        var after = new RunSearchCursor(From, Guid.NewGuid());
        var query = new RunSearchQuery { After = after, IncludeTotal = true };

        // Act
        var command = PostgreSqlRunSearchCommandBuilder.Build(query);

        // Assert
        Assert.Contains("(fr.started_at < @AfterStartedAt OR (fr.started_at = @AfterStartedAt AND fr.id > @AfterId))", command.Sql);
        var count = command.Sql[command.Sql.IndexOf("SELECT COUNT(*)", StringComparison.Ordinal)..];
        Assert.DoesNotContain("@AfterId", count);
        Assert.Equal(TimeSpan.Zero, Param<DateTimeOffset>(command, "AfterStartedAt").Offset);
    }

    [Fact]
    public void Quick_search_matches_the_indexed_expression_with_a_lower_cased_term()
    {
        // Arrange
        var query = new RunSearchQuery { Text = " Order_100% ", StartedFrom = From };

        // Act
        var command = PostgreSqlRunSearchCommandBuilder.Build(query);

        // Assert — the query must repeat the index expression verbatim, or PostgreSQL cannot use the index.
        Assert.Contains(PostgreSqlRunSearchCommandBuilder.RunSearchExpression("fr.") + " LIKE @LowerLike", command.Sql);
        Assert.Equal("%order\\_100\\%%", Param<string>(command, "LowerLike"));
        Assert.DoesNotContain("flow_steps", command.Sql);
        Assert.DoesNotContain("COALESCE(fr.flow_name, '') ILIKE", command.Sql);
    }

    [Fact]
    public void Index_and_query_use_the_same_expression()
    {
        // Arrange

        // Act
        var indexSql = PostgreSqlFlowOrchestratorMigrator.CreateRunSearchIndexSql;

        // Assert
        Assert.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_flow_runs_run_search_trgm", indexSql);
        Assert.Contains("((" + PostgreSqlRunSearchCommandBuilder.RunSearchExpression(string.Empty) + ") gin_trgm_ops)", indexSql);
        Assert.Equal(
            PostgreSqlRunSearchCommandBuilder.RunSearchExpression(string.Empty),
            PostgreSqlRunSearchCommandBuilder.RunSearchExpression("fr.").Replace("fr.", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Term_containing_the_separator_matches_column_by_column()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "Flow|manual" };

        // Act
        var command = PostgreSqlRunSearchCommandBuilder.Build(query);

        // Assert
        Assert.DoesNotContain("'|'", command.Sql);
        Assert.Contains("lower(fr.flow_name) LIKE @LowerLike", command.Sql);
    }

    [Fact]
    public void Run_id_prefix_is_its_own_key_range_statement_ignoring_the_window()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "BBDAFE3E", StartedFrom = From, Take = 50 };

        // Act
        var command = PostgreSqlRunSearchCommandBuilder.Build(query);

        // Assert
        var first = command.Sql[..command.Sql.IndexOf(';', StringComparison.Ordinal)];
        Assert.Contains("fr.id BETWEEN @IdLow AND @IdHigh", first);
        Assert.DoesNotContain("@StartedFrom", first);
        Assert.DoesNotContain("UNION", command.Sql);
        Assert.Equal(new Guid("bbdafe3e-0000-0000-0000-000000000000"), Param<Guid>(command, "IdLow"));
        Assert.Equal(new Guid("bbdafe3e-ffff-ffff-ffff-ffffffffffff"), Param<Guid>(command, "IdHigh"));
        Assert.True(command.Merge);
        Assert.Equal(2, command.RowSets);
        Assert.Equal(51, Param<int>(command, "MergeTake"));
    }

    [Fact]
    public void Full_guid_text_is_a_key_lookup_statement()
    {
        // Arrange
        var id = Guid.NewGuid();

        // Act
        var command = PostgreSqlRunSearchCommandBuilder.Build(new RunSearchQuery { Text = id.ToString("D") });

        // Assert
        Assert.Contains("fr.id = @TextId", command.Sql);
        Assert.Equal(id, Param<Guid>(command, "TextId"));
        Assert.True(command.Merge);
    }

    [Theory]
    [InlineData("bbdafe3e-12", "bbdafe3e-1200-0000-0000-000000000000", "bbdafe3e-12ff-ffff-ffff-ffffffffffff")]
    [InlineData("1234", "12340000-0000-0000-0000-000000000000", "1234ffff-ffff-ffff-ffff-ffffffffffff")]
    public void Prefix_maps_to_the_inclusive_key_range(string prefix, string low, string high)
    {
        // Arrange

        // Act
        var ok = PostgreSqlRunSearchCommandBuilder.TryRunIdRange(prefix, out var lo, out var hi);

        // Assert
        Assert.True(ok);
        Assert.Equal(new Guid(low), lo);
        Assert.Equal(new Guid(high), hi);
    }

    [Theory]
    [InlineData("bbdafe3e12")]      // hyphen missing where the canonical form has one (SQL Server IdText would not match either)
    [InlineData("bb-dafe3e")]       // hyphen in the wrong place
    [InlineData("abc")]             // too short
    [InlineData("backfill")]        // not hex
    public void Non_canonical_prefixes_get_no_key_range(string text)
    {
        // Arrange

        // Act
        var ok = PostgreSqlRunSearchCommandBuilder.TryRunIdRange(text, out _, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    public void Deep_search_probes_steps_with_a_correlated_exists_over_bare_columns()
    {
        // Arrange
        var query = new RunSearchQuery { Text = "INV-001353358", IncludeStepPayloads = true, StartedFrom = From };

        // Act
        var command = PostgreSqlRunSearchCommandBuilder.Build(query);

        // Assert
        Assert.Contains("EXISTS (SELECT 1 FROM flow_steps AS fs WHERE fs.run_id = fr.id AND (fs.step_key ILIKE @SearchLike OR fs.error_message ILIKE @SearchLike OR fs.output_json ILIKE @SearchLike))", command.Sql);
        Assert.DoesNotContain("fr.id IN (", command.Sql);
        Assert.Equal("%INV-001353358%", Param<string>(command, "SearchLike"));
    }

    [Fact]
    public void Merge_orders_ties_by_postgresql_uuid_byte_order_and_applies_the_offset()
    {
        // Arrange — PostgreSQL compares uuid bytewise in canonical order.
        var at = From;
        var low = new FlowRunRecord { Id = new Guid("00000000-0000-0000-0000-0000000000ff"), StartedAt = at };
        var high = new FlowRunRecord { Id = new Guid("00000001-0000-0000-0000-000000000000"), StartedAt = at };
        var newest = new FlowRunRecord { Id = Guid.NewGuid(), StartedAt = at.AddMinutes(1) };
        IReadOnlyList<FlowRunRecord> ids = [high, newest];
        IReadOnlyList<FlowRunRecord> text = [newest, low];

        // Act
        var all = PostgreSqlRunSearchCommandBuilder.Merge([ids, text], skip: 0, take: 10);
        var page = PostgreSqlRunSearchCommandBuilder.Merge([ids, text], skip: 1, take: 0);

        // Assert
        Assert.Equal(new[] { newest.Id, low.Id, high.Id }, all.Select(r => r.Id));
        Assert.Equal(new[] { low.Id }, page.Select(r => r.Id));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("c:\\temp", "c:\\\\temp")]
    public void EscapeLike_makes_wildcards_literal(string value, string expected)
    {
        // Arrange

        // Act
        var escaped = PostgreSqlRunSearchCommandBuilder.EscapeLike(value);

        // Assert
        Assert.Equal(expected, escaped);
    }
}
