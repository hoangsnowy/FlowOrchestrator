using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SqlServer;

public static class SqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers SQL Server implementations of IFlowStore, IFlowRunStore, and IOutputsRepository.
    /// Use inside AddFlowOrchestrator: options.UseSqlServer(connectionString).
    /// </summary>
    public static FlowOrchestratorBuilder UseSqlServer(
        this FlowOrchestratorBuilder builder,
        string connectionString)
    {
        builder.Services.AddSingleton<IFlowStore>(_ => new SqlFlowStore(connectionString));
        builder.Services.AddSingleton<IFlowRunStore>(_ => new SqlFlowRunStore(connectionString));
        builder.Services.AddSingleton<IOutputsRepository>(_ => new SqlOutputsRepository(connectionString));
        builder.Services.AddHostedService(sp =>
            new FlowOrchestratorSqlMigrator(connectionString, sp.GetRequiredService<ILogger<FlowOrchestratorSqlMigrator>>()));
        return builder;
    }

    /// <summary>
    /// Registers SQL Server implementations directly on IServiceCollection.
    /// </summary>
    public static IServiceCollection AddFlowOrchestratorSqlServer(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<IFlowStore>(_ => new SqlFlowStore(connectionString));
        services.AddSingleton<IFlowRunStore>(_ => new SqlFlowRunStore(connectionString));
        services.AddSingleton<IOutputsRepository>(_ => new SqlOutputsRepository(connectionString));
        services.AddHostedService(sp =>
            new FlowOrchestratorSqlMigrator(connectionString, sp.GetRequiredService<ILogger<FlowOrchestratorSqlMigrator>>()));
        return services;
    }
}
