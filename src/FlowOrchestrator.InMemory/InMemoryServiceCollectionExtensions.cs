using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.InMemory;

public static class InMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers in-memory implementations of IFlowStore, IFlowRunStore, and IOutputsRepository.
    /// All data is lost when the process restarts. Use for testing or lightweight scenarios only.
    /// </summary>
    public static FlowOrchestratorBuilder UseInMemory(this FlowOrchestratorBuilder builder)
    {
        builder.Services.AddSingleton<IFlowStore, InMemoryFlowStore>();
        builder.Services.AddSingleton<IFlowRunStore, InMemoryFlowRunStore>();
        builder.Services.AddSingleton<IOutputsRepository, InMemoryOutputsRepository>();
        return builder;
    }
}
