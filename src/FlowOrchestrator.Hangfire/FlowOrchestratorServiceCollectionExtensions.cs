using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire;

public static class FlowOrchestratorServiceCollectionExtensions
{
    public static FlowOrchestratorBuilder AddFlowOrchestrator(
        this IServiceCollection services,
        Action<FlowOrchestratorBuilder> configure)
    {
        var builder = new FlowOrchestratorBuilder(services);

        // Storage backends (UseSqlServer / UsePostgreSql / UseInMemory) register
        // IFlowStore, IFlowRunStore, IOutputsRepository inside this callback.
        configure(builder);

        // Validate that a backend was explicitly configured.
        if (!services.Any(sd => sd.ServiceType == typeof(IFlowStore)))
            throw new InvalidOperationException(
                "No FlowOrchestrator storage backend registered. " +
                "Call options.UseSqlServer(), options.UsePostgreSql(), or options.UseInMemory() " +
                "inside AddFlowOrchestrator(options => ...).");

        services.AddScoped<IExecutionContextAccessor, ExecutionContextAccessor>();

        services.AddSingleton<IFlowRepository>(sp =>
        {
            var repo = new InMemoryFlowRepository();
            foreach (var flow in sp.GetServices<IFlowDefinition>())
                repo.Add(flow);
            return repo;
        });

        services.AddHostedService<FlowSyncHostedService>();

        services.AddTransient<IFlowExecutor, FlowExecutor>();
        services.AddTransient<IStepExecutor, DefaultStepExecutor>();

        services.AddTransient<IHangfireFlowTrigger, HangfireFlowOrchestrator>();
        services.AddTransient<IHangfireStepRunner, HangfireFlowOrchestrator>();
        services.AddSingleton<IRecurringTriggerSync, RecurringTriggerSync>();

        services.AddStepHandler<ForEachStepHandler>("ForEach");

        return builder;
    }
}
