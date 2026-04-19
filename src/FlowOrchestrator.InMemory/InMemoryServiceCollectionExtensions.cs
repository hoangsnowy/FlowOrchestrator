using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// DI registration helpers for the in-memory FlowOrchestrator storage backend.
/// </summary>
public static class InMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers in-memory implementations of all storage interfaces.
    /// All data is lost when the process restarts. Use for testing or lightweight local scenarios only.
    /// </summary>
    /// <param name="builder">The FlowOrchestrator builder to register into.</param>
    public static FlowOrchestratorBuilder UseInMemory(this FlowOrchestratorBuilder builder)
    {
        builder.Services.AddSingleton<IFlowStore, InMemoryFlowStore>();
        builder.Services.AddSingleton<InMemoryFlowRunStore>();
        builder.Services.AddSingleton<IFlowRunStore>(sp => sp.GetRequiredService<InMemoryFlowRunStore>());
        builder.Services.AddSingleton<IFlowRunRuntimeStore>(sp => sp.GetRequiredService<InMemoryFlowRunStore>());
        builder.Services.AddSingleton<IFlowRunControlStore>(sp => sp.GetRequiredService<InMemoryFlowRunStore>());
        builder.Services.AddSingleton<IFlowRetentionStore>(sp => sp.GetRequiredService<InMemoryFlowRunStore>());

        builder.Services.AddSingleton<InMemoryOutputsRepository>();
        builder.Services.AddSingleton<IOutputsRepository>(sp => sp.GetRequiredService<InMemoryOutputsRepository>());
        builder.Services.AddSingleton<IFlowEventReader>(sp => sp.GetRequiredService<InMemoryOutputsRepository>());

        builder.Services.AddSingleton<IFlowScheduleStateStore, InMemoryFlowScheduleStateStore>();
        return builder;
    }
}
