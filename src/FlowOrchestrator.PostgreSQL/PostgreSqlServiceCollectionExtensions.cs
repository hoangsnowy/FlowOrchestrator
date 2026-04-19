using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.PostgreSQL;

public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers PostgreSQL implementations of IFlowStore, IFlowRunStore, and IOutputsRepository.
    /// Use inside AddFlowOrchestrator: options.UsePostgreSql(connectionString).
    /// </summary>
    public static FlowOrchestratorBuilder UsePostgreSql(
        this FlowOrchestratorBuilder builder,
        string connectionString)
    {
        builder.Services.AddSingleton<IFlowStore>(_ => new PostgreSqlFlowStore(connectionString));
        builder.Services.AddSingleton<IFlowRunStore>(_ => new PostgreSqlFlowRunStore(connectionString));
        builder.Services.AddSingleton<IOutputsRepository>(_ => new PostgreSqlOutputsRepository(connectionString));
        builder.Services.AddHostedService(sp =>
            new PostgreSqlFlowOrchestratorMigrator(connectionString, sp.GetRequiredService<ILogger<PostgreSqlFlowOrchestratorMigrator>>()));
        return builder;
    }

    /// <summary>
    /// Registers PostgreSQL implementations directly on IServiceCollection.
    /// Call this after AddFlowOrchestrator when you need direct service registration.
    /// </summary>
    public static IServiceCollection AddFlowOrchestratorPostgreSql(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<IFlowStore>(_ => new PostgreSqlFlowStore(connectionString));
        services.AddSingleton<IFlowRunStore>(_ => new PostgreSqlFlowRunStore(connectionString));
        services.AddSingleton<IOutputsRepository>(_ => new PostgreSqlOutputsRepository(connectionString));
        services.AddHostedService(sp =>
            new PostgreSqlFlowOrchestratorMigrator(connectionString, sp.GetRequiredService<ILogger<PostgreSqlFlowOrchestratorMigrator>>()));
        return services;
    }
}
