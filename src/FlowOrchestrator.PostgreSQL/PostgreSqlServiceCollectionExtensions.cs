using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// DI registration helpers for the PostgreSQL FlowOrchestrator storage backend.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers PostgreSQL implementations of all storage interfaces and the auto-migration hosted service.
    /// Call inside <c>AddFlowOrchestrator(options => options.UsePostgreSql(connectionString))</c>.
    /// </summary>
    /// <param name="builder">The FlowOrchestrator builder to register into.</param>
    /// <param name="connectionString">Npgsql connection string. Must point to an accessible PostgreSQL database.</param>
    public static FlowOrchestratorBuilder UsePostgreSql(
        this FlowOrchestratorBuilder builder,
        string connectionString)
    {
        builder.Services.AddSingleton<IFlowStore>(_ => new PostgreSqlFlowStore(connectionString));

        builder.Services.AddSingleton<PostgreSqlFlowRunStore>(_ => new PostgreSqlFlowRunStore(connectionString));
        builder.Services.AddSingleton<IFlowRunStore>(sp => sp.GetRequiredService<PostgreSqlFlowRunStore>());
        builder.Services.AddSingleton<IFlowRunRuntimeStore>(sp => sp.GetRequiredService<PostgreSqlFlowRunStore>());
        builder.Services.AddSingleton<IFlowRunControlStore>(sp => sp.GetRequiredService<PostgreSqlFlowRunStore>());
        builder.Services.AddSingleton<IFlowRetentionStore>(sp => sp.GetRequiredService<PostgreSqlFlowRunStore>());

        builder.Services.AddSingleton<PostgreSqlOutputsRepository>(_ => new PostgreSqlOutputsRepository(connectionString));
        builder.Services.AddSingleton<IOutputsRepository>(sp => sp.GetRequiredService<PostgreSqlOutputsRepository>());
        builder.Services.AddSingleton<IFlowEventReader>(sp => sp.GetRequiredService<PostgreSqlOutputsRepository>());

        builder.Services.AddSingleton<IFlowSignalStore>(_ => new PostgreSqlFlowSignalStore(connectionString));

        builder.Services.AddSingleton<IFlowScheduleStateStore>(_ => new PostgreSqlFlowScheduleStateStore(connectionString));
        builder.Services.AddHostedService(sp =>
            new PostgreSqlFlowOrchestratorMigrator(connectionString, sp.GetRequiredService<ILogger<PostgreSqlFlowOrchestratorMigrator>>()));
        return builder;
    }

    /// <summary>
    /// Registers the PostgreSQL-backed webhook hardening stores
    /// (<see cref="IWebhookReplayStore"/>, <see cref="IWebhookRejectionStore"/>),
    /// replacing the in-memory defaults wired by <c>AddFlowDashboard</c>.
    /// </summary>
    /// <param name="builder">The FlowOrchestrator builder.</param>
    /// <param name="connectionString">Npgsql connection string.</param>
    public static FlowOrchestratorBuilder AddPostgreSqlWebhookHardening(
        this FlowOrchestratorBuilder builder,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Connection string required.", nameof(connectionString));
        builder.Services.Replace(ServiceDescriptor.Singleton<IWebhookReplayStore>(_ => new PostgreSqlWebhookReplayStore(connectionString)));
        builder.Services.Replace(ServiceDescriptor.Singleton<IWebhookRejectionStore>(_ => new PostgreSqlWebhookRejectionStore(connectionString)));
        return builder;
    }
}
