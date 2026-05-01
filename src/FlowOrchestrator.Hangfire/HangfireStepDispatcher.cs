using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Hangfire;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Hangfire implementation of <see cref="IStepDispatcher"/> that submits steps
/// as Hangfire background jobs targeting <see cref="IHangfireStepRunner"/>.
/// </summary>
internal sealed class HangfireStepDispatcher : IStepDispatcher
{
    private readonly IBackgroundJobClient _client;

    /// <summary>Initialises the dispatcher with the Hangfire job client.</summary>
    public HangfireStepDispatcher(IBackgroundJobClient client) => _client = client;

    /// <inheritdoc/>
    public ValueTask<string?> EnqueueStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        CancellationToken ct)
    {
        // Pass only flow.Id through Hangfire's argument store. The runner rehydrates the
        // full IFlowDefinition via IFlowRepository — this avoids serialising the manifest
        // (and types like RunAfterCondition) through Hangfire's Newtonsoft.Json serializer.
        var flowId = flow.Id;
        var id = _client.Enqueue<IHangfireStepRunner>(
            r => r.RunStepAsync(context, flowId, step, null));
        return new(id);
    }

    /// <inheritdoc/>
    public ValueTask<string?> ScheduleStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        TimeSpan delay,
        CancellationToken ct)
    {
        var flowId = flow.Id;
        var id = _client.Schedule<IHangfireStepRunner>(
            r => r.RunStepAsync(context, flowId, step, null),
            delay);
        return new(id);
    }
}
