using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// DI registration helpers for the SQL Server FlowOrchestrator storage backend.
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers SQL Server implementations of all storage interfaces and the auto-migration hosted service.
    /// Call inside <c>AddFlowOrchestrator(options => options.UseSqlServer(connectionString))</c>.
    /// </summary>
    /// <param name="builder">The FlowOrchestrator builder to register into.</param>
    /// <param name="connectionString">SQL Server connection string. Must point to an accessible database.</param>
    public static FlowOrchestratorBuilder UseSqlServer(
        this FlowOrchestratorBuilder builder,
        string connectionString)
    {
        builder.Services.AddSingleton<IFlowStore>(_ => new SqlFlowStore(connectionString));

        builder.Services.AddSingleton<SqlFlowRunStore>(_ => new SqlFlowRunStore(connectionString));
        builder.Services.AddSingleton<IFlowRunStore>(sp => sp.GetRequiredService<SqlFlowRunStore>());
        builder.Services.AddSingleton<IFlowRunRuntimeStore>(sp => sp.GetRequiredService<SqlFlowRunStore>());
        builder.Services.AddSingleton<IFlowRunControlStore>(sp => sp.GetRequiredService<SqlFlowRunStore>());
        builder.Services.AddSingleton<IFlowRetentionStore>(sp => sp.GetRequiredService<SqlFlowRunStore>());

        builder.Services.AddSingleton<SqlOutputsRepository>(_ => new SqlOutputsRepository(connectionString));
        builder.Services.AddSingleton<IOutputsRepository>(sp => sp.GetRequiredService<SqlOutputsRepository>());
        builder.Services.AddSingleton<IFlowEventReader>(sp => sp.GetRequiredService<SqlOutputsRepository>());

        builder.Services.AddSingleton<IFlowSignalStore>(_ => new SqlFlowSignalStore(connectionString));

        builder.Services.AddSingleton<IFlowScheduleStateStore>(_ => new SqlFlowScheduleStateStore(connectionString));
        builder.Services.AddHostedService(sp =>
            new FlowOrchestratorSqlMigrator(connectionString, sp.GetRequiredService<ILogger<FlowOrchestratorSqlMigrator>>()));
        RegisterFlowRepository(builder.Services);
        return builder;
    }

    /// <summary>
    /// Registers SQL Server implementations directly on <see cref="IServiceCollection"/>,
    /// for use outside the <c>AddFlowOrchestrator</c> builder pattern.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    public static IServiceCollection AddFlowOrchestratorSqlServer(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<IFlowStore>(_ => new SqlFlowStore(connectionString));

        services.AddSingleton<SqlFlowRunStore>(_ => new SqlFlowRunStore(connectionString));
        services.AddSingleton<IFlowRunStore>(sp => sp.GetRequiredService<SqlFlowRunStore>());
        services.AddSingleton<IFlowRunRuntimeStore>(sp => sp.GetRequiredService<SqlFlowRunStore>());
        services.AddSingleton<IFlowRunControlStore>(sp => sp.GetRequiredService<SqlFlowRunStore>());
        services.AddSingleton<IFlowRetentionStore>(sp => sp.GetRequiredService<SqlFlowRunStore>());

        services.AddSingleton<SqlOutputsRepository>(_ => new SqlOutputsRepository(connectionString));
        services.AddSingleton<IOutputsRepository>(sp => sp.GetRequiredService<SqlOutputsRepository>());
        services.AddSingleton<IFlowEventReader>(sp => sp.GetRequiredService<SqlOutputsRepository>());

        services.AddSingleton<IFlowSignalStore>(_ => new SqlFlowSignalStore(connectionString));

        services.AddSingleton<IFlowScheduleStateStore>(_ => new SqlFlowScheduleStateStore(connectionString));
        services.AddHostedService(sp =>
            new FlowOrchestratorSqlMigrator(connectionString, sp.GetRequiredService<ILogger<FlowOrchestratorSqlMigrator>>()));
        RegisterFlowRepository(services);
        return services;
    }

    // IFlowRepository is the in-process registry of code-defined flow classes.
    // Each storage backend is responsible for registering it; AddFlowOrchestrator validates
    // a registration exists. Trivial private impl avoids cross-project deps.
    private static void RegisterFlowRepository(IServiceCollection services)
    {
        services.TryAddSingleton<IFlowRepository>(sp =>
            new SqlServerFlowRegistry(sp.GetServices<IFlowDefinition>()));
    }

    private sealed class SqlServerFlowRegistry : IFlowRepository
    {
        private readonly IReadOnlyList<IFlowDefinition> _flows;
        public SqlServerFlowRegistry(IEnumerable<IFlowDefinition> flows) => _flows = flows.ToList();
        public ValueTask<IReadOnlyList<IFlowDefinition>> GetAllFlowsAsync() => ValueTask.FromResult(_flows);
    }
}
