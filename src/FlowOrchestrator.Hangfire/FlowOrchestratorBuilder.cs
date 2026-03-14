using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire;

public sealed class FlowOrchestratorBuilder
{
    public FlowOrchestratorBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }
    internal FlowOrchestratorOptions Options { get; } = new();
    internal bool HangfireEnabled { get; private set; }

    public FlowOrchestratorBuilder UseSqlServer(string connectionString)
    {
        Options.ConnectionString = connectionString;
        return this;
    }

    public FlowOrchestratorBuilder UseHangfire()
    {
        HangfireEnabled = true;
        return this;
    }

    public FlowOrchestratorBuilder AddFlow<TFlow>() where TFlow : class, IFlowDefinition, new()
    {
        Services.AddSingleton<IFlowDefinition, TFlow>();
        return this;
    }
}
