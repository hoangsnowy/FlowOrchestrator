using Microsoft.Data.SqlClient;
using System.Data;

namespace FlowOrchestrator.SampleApp.Steps;

public sealed class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(string connectionString) => _connectionString = connectionString;

    public IDbConnection Create() => new SqlConnection(_connectionString);
}
