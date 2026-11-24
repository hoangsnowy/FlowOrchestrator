namespace FlowOrchestrator.StorageMigration;

/// <summary>A FlowOrchestrator storage backend that <see cref="FlowStorageCopier"/> can read from or write to.</summary>
public enum FlowStorageProvider
{
    /// <summary>SQL Server / Azure SQL, the schema created by <c>FlowOrchestratorSqlMigrator</c> (<c>UseSqlServer</c>).</summary>
    SqlServer,

    /// <summary>PostgreSQL, the schema created by <c>PostgreSqlFlowOrchestratorMigrator</c> (<c>UsePostgreSql</c>).</summary>
    PostgreSql,
}

/// <summary>Parses the provider names accepted on the command line and in configuration.</summary>
public static class FlowStorageProviderNames
{
    /// <summary>
    /// Parses <paramref name="value"/> (case-insensitive): <c>sqlserver</c>, <c>mssql</c>, <c>azuresql</c>
    /// for <see cref="FlowStorageProvider.SqlServer"/>; <c>postgresql</c>, <c>postgres</c>, <c>pg</c>,
    /// <c>npgsql</c> for <see cref="FlowStorageProvider.PostgreSql"/>.
    /// </summary>
    /// <param name="value">The provider name.</param>
    /// <param name="provider">The parsed provider when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> names a supported provider.</returns>
    public static bool TryParse(string? value, out FlowStorageProvider provider)
    {
        var span = value.AsSpan().Trim();
        if (span.Equals("sqlserver", StringComparison.OrdinalIgnoreCase)
            || span.Equals("mssql", StringComparison.OrdinalIgnoreCase)
            || span.Equals("azuresql", StringComparison.OrdinalIgnoreCase))
        {
            provider = FlowStorageProvider.SqlServer;
            return true;
        }

        if (span.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
            || span.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            || span.Equals("pg", StringComparison.OrdinalIgnoreCase)
            || span.Equals("npgsql", StringComparison.OrdinalIgnoreCase))
        {
            provider = FlowStorageProvider.PostgreSql;
            return true;
        }

        provider = default;
        return false;
    }

    /// <summary>Returns the canonical command-line name of <paramref name="provider"/> (<c>sqlserver</c> / <c>postgresql</c>).</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The canonical lower-case name.</returns>
    public static string ToName(FlowStorageProvider provider) => provider switch
    {
        FlowStorageProvider.SqlServer => "sqlserver",
        FlowStorageProvider.PostgreSql => "postgresql",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };
}
