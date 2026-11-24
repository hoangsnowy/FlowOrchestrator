using FlowOrchestrator.StorageMigration.Internal;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.UnitTests;

/// <summary>Copier behaviour that needs no database: option validation, same-database refusal, result summaries.</summary>
public sealed class FlowStorageCopierTests
{
    private static FlowStorageCopyOptions Options(string source, string target, FlowStorageProvider from = FlowStorageProvider.SqlServer, FlowStorageProvider to = FlowStorageProvider.SqlServer) => new()
    {
        SourceProvider = from,
        SourceConnectionString = source,
        TargetProvider = to,
        TargetConnectionString = target,
    };

    [Fact]
    public async Task Copying_a_SQL_Server_database_onto_itself_is_refused_before_any_connection_opens()
    {
        // Arrange — an unreachable host proves nothing is opened: the refusal comes first.
        var copier = new FlowStorageCopier(Options(
            "Server=tcp:no-such-host.invalid,1433;Database=Flows;User Id=a;Password=b",
            "Server=NO-SUCH-HOST.invalid;Database=flows;User Id=c;Password=d"));

        // Act
        var ex = await Assert.ThrowsAsync<FlowStoragePreflightException>(() => copier.CopyAsync());

        // Assert
        Assert.Equal(FlowStoragePreflightFailure.SameDatabase, ex.Failure);
    }

    [Fact]
    public async Task Copying_a_PostgreSQL_database_onto_itself_is_refused()
    {
        // Arrange
        var copier = new FlowStorageCopier(Options(
            "Host=no-such-host.invalid;Port=5432;Database=flows;Username=a;Password=b",
            "Host=NO-SUCH-HOST.invalid;Database=Flows;Username=c;Password=d",
            FlowStorageProvider.PostgreSql, FlowStorageProvider.PostgreSql));

        // Act
        var ex = await Assert.ThrowsAsync<FlowStoragePreflightException>(() => copier.VerifyAsync());

        // Assert
        Assert.Equal(FlowStoragePreflightFailure.SameDatabase, ex.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FlowStorageCopyOptions.MaxBatchSize + 1)]
    public async Task An_out_of_range_batch_size_is_rejected(int batchSize)
    {
        // Arrange
        var options = Options("Server=a;Database=b", "Host=c;Database=d", to: FlowStorageProvider.PostgreSql);
        options.BatchSize = batchSize;
        var copier = new FlowStorageCopier(options);

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => copier.CopyAsync());

        // Assert
        Assert.Equal("BatchSize", ex.ParamName);
    }

    [Fact]
    public async Task A_missing_connection_string_is_rejected()
    {
        // Arrange
        var copier = new FlowStorageCopier(Options(" ", "Host=c;Database=d", to: FlowStorageProvider.PostgreSql));

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => copier.CopyAsync());

        // Assert
        Assert.Equal("SourceConnectionString", ex.ParamName);
    }

    [Theory]
    [InlineData("tcp:Sql01,1433", "FlowDb", "sql01", "flowdb")]
    [InlineData(".", "FlowDb", "localhost", "flowdb")]
    [InlineData("(local)", "x", "localhost", "X")]
    public void SQL_Server_identities_ignore_case_protocol_prefix_and_default_port(string server, string db, string otherServer, string otherDb)
    {
        // Arrange
        var left = SqlServerEndpoint.NormalizeIdentity(server, db);

        // Act
        var right = SqlServerEndpoint.NormalizeIdentity(otherServer, otherDb);

        // Assert
        Assert.Equal(left, right);
    }

    [Theory]
    [InlineData("Server=s;Database=d", new string[0])]
    [InlineData("Server=s;Database=d;Encrypt=True", new string[0])]
    [InlineData("Server=s;Database=d;Encrypt=Strict;TrustServerCertificate=True", new string[0])]
    [InlineData("Server=s;Database=d;Encrypt=False", new[] { "Encrypt=False/Optional" })]
    [InlineData("Server=s;Database=d;Encrypt=Optional", new[] { "Encrypt=False/Optional" })]
    [InlineData("Server=s;Database=d;TrustServerCertificate=True", new[] { "TrustServerCertificate=True" })]
    [InlineData("Server=s;Database=d;Encrypt=False;TrustServerCertificate=True", new[] { "Encrypt=False/Optional", "TrustServerCertificate=True" })]
    public async Task A_SQL_Server_connection_that_weakens_transport_security_is_described_not_changed(string connectionString, string[] expected)
    {
        // Arrange
        await using var endpoint = new SqlServerEndpoint(connectionString, 30);

        // Act
        var warnings = endpoint.DescribeTransportWeaknesses("target");

        // Assert — one sentence per weakness, naming the side and the server, never rewriting the connection string.
        Assert.Equal(expected.Length, warnings.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Contains(expected[i], warnings[i], StringComparison.Ordinal);
            Assert.StartsWith("The target connection (SQL Server s / d)", warnings[i], StringComparison.Ordinal);
        }

        Assert.Equal(connectionString, endpoint.ConnectionString);
    }

    [Fact]
    public void A_result_is_verified_only_when_every_verified_table_matched()
    {
        // Arrange
        FlowStorageTableResult Table(bool? verified) => new() { Table = "T", SourceName = "T", TargetName = "t", Verified = verified };

        // Act
        var allMatched = new FlowStorageCopyResult { Tables = [Table(true), Table(true)], Notes = [] };
        var oneDiffers = new FlowStorageCopyResult { Tables = [Table(true), Table(false)], Notes = [] };
        var noneVerified = new FlowStorageCopyResult { Tables = [Table(null)], Notes = [] };

        // Assert
        Assert.True(allMatched.IsVerified);
        Assert.False(oneDiffers.IsVerified);
        Assert.False(noneVerified.IsVerified);
    }

    [Theory]
    [InlineData("sqlserver", FlowStorageProvider.SqlServer)]
    [InlineData("MSSQL", FlowStorageProvider.SqlServer)]
    [InlineData(" postgresql ", FlowStorageProvider.PostgreSql)]
    [InlineData("pg", FlowStorageProvider.PostgreSql)]
    [InlineData("Postgres", FlowStorageProvider.PostgreSql)]
    public void Provider_names_parse_case_insensitively(string name, FlowStorageProvider expected)
    {
        // Arrange
        var input = name;

        // Act
        var parsed = FlowStorageProviderNames.TryParse(input, out var provider);

        // Assert
        Assert.True(parsed);
        Assert.Equal(expected, provider);
    }

    [Fact]
    public void An_unknown_provider_name_does_not_parse()
    {
        // Arrange
        const string input = "mysql";

        // Act
        var parsed = FlowStorageProviderNames.TryParse(input, out _);

        // Assert
        Assert.False(parsed);
    }

    [Fact]
    public void The_bulk_load_reader_exposes_the_batch_with_nulls_as_DBNull()
    {
        // Arrange
        var table = StorageCatalog.FlowStepClaims;
        var runId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        using var reader = new RowBatchDataReader(table, [[runId, "a", at], [runId, "b", null]]);

        // Act
        var first = reader.Read();
        var firstValues = new object[3];
        reader.GetValues(firstValues);
        var second = reader.Read();
        var secondIsNull = reader.IsDBNull(2);
        var secondValue = reader.GetValue(2);
        var third = reader.Read();

        // Assert
        Assert.True(first);
        Assert.Equal([runId, "a", at], firstValues);
        Assert.True(second);
        Assert.True(secondIsNull);
        Assert.Same(DBNull.Value, secondValue);
        Assert.False(third);
        Assert.Equal(3, reader.FieldCount);
        Assert.Equal("ClaimedAt", reader.GetName(2));
        Assert.Equal(typeof(DateTimeOffset), reader.GetFieldType(2));
    }
}
