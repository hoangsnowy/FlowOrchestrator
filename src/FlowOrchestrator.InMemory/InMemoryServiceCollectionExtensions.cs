using System.Threading.Channels;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// DI registration helpers for the in-memory FlowOrchestrator storage backend and runtime.
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

    /// <summary>
    /// Registers the in-memory step-execution runtime: a <see cref="Channel{T}"/>-based
    /// dispatcher and a background-service consumer that calls
    /// <c>IFlowOrchestrator.RunStepAsync</c> for each dispatched step.
    /// </summary>
    /// <remarks>
    /// Call this inside <c>AddFlowOrchestrator(opts =&gt; opts.UseInMemoryRuntime())</c>
    /// as an alternative to Hangfire. Cron-triggered flows are not supported — all
    /// <see cref="IRecurringTriggerDispatcher"/> calls are silently ignored.
    /// <para>
    /// The runtime is registered with <see cref="ServiceCollectionDescriptorExtensions.TryAdd"/>
    /// semantics so it can co-exist with other registrations without conflicting.
    /// </para>
    /// </remarks>
    /// <param name="builder">The FlowOrchestrator builder to register into.</param>
    /// <returns>The same builder for chaining.</returns>
    public static FlowOrchestratorBuilder UseInMemoryRuntime(this FlowOrchestratorBuilder builder)
    {
        // Shared unbounded channel — dispatcher writes, runner reads.
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        builder.Services.AddSingleton(channel);
        builder.Services.AddSingleton(channel.Reader);
        builder.Services.AddSingleton(channel.Writer);

        // Runtime adapters — registered with TryAdd so they don't conflict if a test
        // registers its own mocks first.
        builder.Services.TryAddSingleton<IStepDispatcher, InMemoryStepDispatcher>();
        builder.Services.TryAddSingleton<IRecurringTriggerDispatcher, NullRecurringTriggerDispatcher>();
        builder.Services.TryAddSingleton<IRecurringTriggerInspector, NullRecurringTriggerInspector>();
        builder.Services.TryAddSingleton<IRecurringTriggerSync, NullRecurringTriggerSync>();

        // Background service that drains the channel and invokes the engine.
        builder.Services.AddHostedService<InMemoryStepRunnerHostedService>();

        return builder;
    }
}
