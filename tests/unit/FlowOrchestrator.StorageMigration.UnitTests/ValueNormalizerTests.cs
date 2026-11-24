using FlowOrchestrator.StorageMigration.Internal;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.UnitTests;

/// <summary>
/// Pins what a value becomes on the other backend: timestamps truncated to the microsecond and moved to UTC
/// toward PostgreSQL, kept as they are toward SQL Server; text the target cannot store rejected with the row
/// and column named.
/// </summary>
public sealed class ValueNormalizerTests
{
    private static readonly StorageTable Steps = StorageCatalog.FlowSteps;
    private static readonly StorageColumn StepKey = Steps.Columns[1];
    private static readonly StorageColumn OutputJson = Steps.Columns[5];
    private static readonly StorageColumn StartedAt = Steps.Columns[8];

    private static object?[] StepRow(string key = "step") =>
        [new Guid("11111111-2222-3333-4444-555555555555"), key, "Type", "Succeeded", null, null, null, null, DateTimeOffset.UnixEpoch, null, null];

    [Fact]
    public void Toward_PostgreSQL_a_timestamp_is_truncated_to_the_microsecond_and_stored_in_UTC()
    {
        // Arrange
        var value = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234_567);

        // Act
        var prepared = (DateTimeOffset)ValueNormalizer.PrepareForTarget(FlowStorageProvider.PostgreSql, Steps, StartedAt, value, StepRow())!;

        // Assert — 0.1234567 s keeps 0.123456 s (truncated, not rounded to .123457); same instant, offset 0.
        Assert.Equal(TimeSpan.Zero, prepared.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero).AddTicks(1_234_560), prepared);
    }

    [Fact]
    public void Toward_SQL_Server_a_timestamp_is_written_as_read()
    {
        // Arrange
        var value = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(-5)).AddTicks(7);

        // Act
        var prepared = (DateTimeOffset)ValueNormalizer.PrepareForTarget(FlowStorageProvider.SqlServer, Steps, StartedAt, value, StepRow())!;

        // Assert
        Assert.Equal(value.Offset, prepared.Offset);
        Assert.Equal(value.UtcTicks, prepared.UtcTicks);
    }

    [Fact]
    public void A_NUL_character_is_rejected_toward_PostgreSQL_with_the_row_and_column_named()
    {
        // Arrange
        var row = StepRow("charge");

        // Act
        var ex = Assert.Throws<FlowStorageCopyException>(() =>
            ValueNormalizer.PrepareForTarget(FlowStorageProvider.PostgreSql, Steps, OutputJson, "{\"a\":\"x\0y\"}", row));

        // Assert
        Assert.Equal("FlowSteps", ex.Table);
        Assert.Contains("RunId=11111111-2222-3333-4444-555555555555, StepKey=charge", ex.Message);
        Assert.Contains("column OutputJson", ex.Message);
        Assert.Contains("U+0000", ex.Message);
    }

    [Fact]
    public void A_NUL_character_is_kept_toward_SQL_Server()
    {
        // Arrange
        const string value = "x\0y";

        // Act
        var prepared = ValueNormalizer.PrepareForTarget(FlowStorageProvider.SqlServer, Steps, OutputJson, value, StepRow());

        // Assert
        Assert.Same(value, prepared);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void An_unpaired_surrogate_is_rejected_toward_PostgreSQL(int shape)
    {
        // Arrange — built in code: a lone surrogate does not survive test-case serialization as a literal.
        var value = shape switch
        {
            0 => "bad " + '\uD83D' + " end",
            1 => "bad " + '\uDE00' + " end",
            _ => "ends high " + '\uD83D',
        };
        var row = StepRow();

        // Act
        var ex = Assert.Throws<FlowStorageCopyException>(() =>
            ValueNormalizer.PrepareForTarget(FlowStorageProvider.PostgreSql, Steps, OutputJson, value, row));

        // Assert
        Assert.Contains("unpaired UTF-16 surrogate", ex.Message);
    }

    [Fact]
    public void Paired_surrogates_and_unicode_text_pass_toward_PostgreSQL_unchanged()
    {
        // Arrange
        const string value = "Tiếng Việt · 日本語 · emoji 😀🚀 · ñ";

        // Act
        var prepared = ValueNormalizer.PrepareForTarget(FlowStorageProvider.PostgreSql, Steps, OutputJson, value, StepRow());

        // Assert
        Assert.Same(value, prepared);
    }

    [Fact]
    public void A_key_of_256_emoji_fits_PostgreSQL_varchar_256_but_not_SQL_Server_nvarchar_256()
    {
        // Arrange — 256 code points, 512 UTF-16 code units.
        var value = string.Concat(Enumerable.Repeat("😀", 256));

        // Act
        var toPostgres = ValueNormalizer.FindPostgresTextProblem(value, StepKey.MaxLength);
        var toSqlServer = ValueNormalizer.FindSqlServerTextProblem(value, StepKey.MaxLength);

        // Assert
        Assert.Null(toPostgres);
        Assert.NotNull(toSqlServer);
        Assert.Contains("NVARCHAR(256)", toSqlServer);
    }

    [Fact]
    public void A_value_longer_than_the_PostgreSQL_column_is_rejected_with_its_length()
    {
        // Arrange
        var value = new string('k', 257);

        // Act
        var problem = ValueNormalizer.FindPostgresTextProblem(value, 256);

        // Assert
        Assert.Equal("the value has 257 characters; the PostgreSQL column holds 256.", problem);
    }

    [Fact]
    public void Unbounded_columns_take_any_length()
    {
        // Arrange
        var value = new string('x', 100_000);

        // Act
        var toPostgres = ValueNormalizer.FindPostgresTextProblem(value, null);
        var toSqlServer = ValueNormalizer.FindSqlServerTextProblem(value, null);

        // Assert
        Assert.Null(toPostgres);
        Assert.Null(toSqlServer);
    }

    [Fact]
    public void Null_and_non_text_values_pass_through()
    {
        // Arrange
        var guid = Guid.NewGuid();

        // Act
        var nullValue = ValueNormalizer.PrepareForTarget(FlowStorageProvider.PostgreSql, Steps, OutputJson, null, StepRow());
        var guidValue = ValueNormalizer.PrepareForTarget(FlowStorageProvider.PostgreSql, Steps, Steps.Columns[0], guid, StepRow());

        // Assert
        Assert.Null(nullValue);
        Assert.Equal(guid, guidValue);
    }
}
