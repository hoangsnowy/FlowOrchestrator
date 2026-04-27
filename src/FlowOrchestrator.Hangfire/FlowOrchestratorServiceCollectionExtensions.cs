using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// DI registration extensions for the FlowOrchestrator runtime.
/// </summary>
public static class FlowOrchestratorServiceCollectionExtensions
{
    /// <summary>
    /// Registers all FlowOrchestrator services, including the execution pipeline, scheduler,
    /// retention sweep, and observability infrastructure.
    /// </summary>
    /// <remarks>
    /// A storage backend must be configured inside the <paramref name="configure"/> callback
    /// via <c>options.UseSqlServer()</c>, <c>options.UsePostgreSql()</c>, or <c>options.UseInMemory()</c>;
    /// otherwise an <see cref="InvalidOperationException"/> is thrown.
    /// Hangfire itself must be registered separately before calling this method.
    /// </remarks>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configure">Callback that receives a <see cref="FlowOrchestratorBuilder"/> for configuration.</param>
    /// <returns>The configured builder, allowing further chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no <c>IFlowStore</c> implementation was registered inside <paramref name="configure"/>.
    /// </exception>
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
        services.AddSingleton<FlowOrchestratorTelemetry>();
        services.AddSingleton(builder.Scheduler);
        services.AddSingleton(builder.RunControl);
        services.AddSingleton(builder.Retention);
        services.AddSingleton(builder.Observability);
        services.TryAddSingleton<IFlowScheduleStateStore, EphemeralFlowScheduleStateStore>();

        services.AddSingleton<IFlowRepository>(sp =>
        {
            var repo = new InMemoryFlowRepository();
            foreach (var flow in sp.GetServices<IFlowDefinition>())
                repo.Add(flow);
            return repo;
        });

        services.AddHostedService<FlowSyncHostedService>();
        services.AddHostedService<FlowRetentionHostedService>();

        services.AddTransient<IFlowExecutor, FlowExecutor>();
        services.AddSingleton<IFlowGraphPlanner, FlowGraphPlanner>();
        services.AddTransient<IStepExecutor, DefaultStepExecutor>();

        services.AddSingleton<IStepDispatcher, HangfireStepDispatcher>();

        services.AddTransient<IHangfireFlowTrigger, HangfireFlowOrchestrator>();
        services.AddTransient<IHangfireStepRunner, HangfireFlowOrchestrator>();
        services.AddSingleton<IRecurringTriggerSync, RecurringTriggerSync>();

        services.AddStepHandler<ForEachStepHandler>("ForEach");

        return builder;
    }
}
