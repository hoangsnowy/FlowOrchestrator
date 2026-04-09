using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire;

public static class FlowOrchestratorServiceCollectionExtensions
{
    public static FlowOrchestratorBuilder AddFlowOrchestrator(this IServiceCollection services, Action<FlowOrchestratorBuilder> configure)
    {
        var builder = new FlowOrchestratorBuilder(services);
        configure(builder);

        services.AddSingleton(builder.Options);

        services.AddScoped<IExecutionContextAccessor, ExecutionContextAccessor>();

        if (!string.IsNullOrEmpty(builder.Options.ConnectionString))
        {
            services.AddFlowOrchestratorSqlServer(builder.Options.ConnectionString);
        }
        else
        {
            services.AddSingleton<IFlowStore, InMemoryFlowStore>();
            services.AddSingleton<IFlowRunStore, InMemoryFlowRunStore>();
            services.AddSingleton<IOutputsRepository, InMemoryOutputsRepository>();
        }

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
