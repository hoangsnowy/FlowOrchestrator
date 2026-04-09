using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SqlServer;

public static class SqlServerServiceCollectionExtensions
{
    public static IServiceCollection AddFlowOrchestratorSqlServer(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<IFlowStore>(_ => new SqlFlowStore(connectionString));
        services.AddSingleton<IFlowRunStore>(_ => new SqlFlowRunStore(connectionString));
        services.AddSingleton<IOutputsRepository>(_ => new SqlOutputsRepository(connectionString));
        services.AddHostedService(sp => new FlowOrchestratorSqlMigrator(connectionString, sp.GetRequiredService<ILogger<FlowOrchestratorSqlMigrator>>()));
        return services;
    }
}
