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

        builder.Services.AddSingleton<IFlowSignalStore>(_ => new SqlFlowSignalStore(connectionString));

        builder.Services.AddSingleton<IFlowScheduleStateStore>(_ => new SqlFlowScheduleStateStore(connectionString));
        builder.Services.AddHostedService(sp =>
            new FlowOrchestratorSqlMigrator(connectionString, sp.GetRequiredService<ILogger<FlowOrchestratorSqlMigrator>>()));
        return builder;
    }

    /// <summary>
    /// Registers the SQL Server-backed webhook hardening stores
    /// (<see cref="IWebhookReplayStore"/>, <see cref="IWebhookRejectionStore"/>).
    /// Call AFTER <see cref="UseSqlServer"/> so the migrator + connection string
    /// are already bound. Defaults from <c>AddFlowDashboard</c> are replaced.
    /// </summary>
    /// <param name="builder">The FlowOrchestrator builder.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    public static FlowOrchestratorBuilder AddSqlServerWebhookHardening(
        this FlowOrchestratorBuilder builder,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Connection string required.", nameof(connectionString));
        builder.Services.Replace(ServiceDescriptor.Singleton<IWebhookReplayStore>(_ => new SqlWebhookReplayStore(connectionString)));
        builder.Services.Replace(ServiceDescriptor.Singleton<IWebhookRejectionStore>(_ => new SqlWebhookRejectionStore(connectionString)));
        return builder;
    }
}
