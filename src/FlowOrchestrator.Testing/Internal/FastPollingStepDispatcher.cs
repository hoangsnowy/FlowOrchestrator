using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Internal;

/// <summary>
/// <see cref="IStepDispatcher"/> decorator that clamps the delay parameter passed to
/// <see cref="ScheduleStepAsync"/> so polling steps re-execute almost immediately in tests.
/// Forwards every other call straight through to the wrapped dispatcher.
/// </summary>
internal sealed class FastPollingStepDispatcher : IStepDispatcher
{
    private readonly IStepDispatcher _inner;
    private readonly TimeSpan _maxDelay;

    public FastPollingStepDispatcher(IStepDispatcher inner, TimeSpan maxDelay)
    {
        _inner = inner;
        _maxDelay = maxDelay;
    }

    /// <inheritdoc/>
    public ValueTask<string?> EnqueueStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        CancellationToken ct = default) =>
        _inner.EnqueueStepAsync(context, flow, step, ct);

    /// <inheritdoc/>
    public ValueTask<string?> ScheduleStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        TimeSpan delay,
        CancellationToken ct = default)
    {
        var clamped = delay > _maxDelay ? _maxDelay : delay;
        return _inner.ScheduleStepAsync(context, flow, step, clamped, ct);
    }
}
