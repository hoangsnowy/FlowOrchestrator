using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// In-process <see cref="IStepDispatcher"/> backed by a <see cref="Channel{T}"/>.
/// Intended for unit tests and lightweight single-process deployments that do not
/// need Hangfire or an external message broker.
/// </summary>
/// <remarks>
/// Immediate enqueues write directly to the channel; delayed enqueues fire a background
/// <see cref="Task.Delay"/> and then write, so <see cref="ScheduleStepAsync"/> returns
/// almost immediately while the step remains invisible to the runner until the delay elapses.
/// </remarks>
internal sealed class InMemoryStepDispatcher : IStepDispatcher
{
    private readonly ChannelWriter<InMemoryStepEnvelope> _writer;

    /// <summary>Initialises the dispatcher with the shared channel writer.</summary>
    public InMemoryStepDispatcher(ChannelWriter<InMemoryStepEnvelope> writer)
    {
        _writer = writer;
    }

    /// <inheritdoc/>
    public async ValueTask<string?> EnqueueStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await _writer.WriteAsync(new InMemoryStepEnvelope(context, flow, step, id), ct).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc/>
    public ValueTask<string?> ScheduleStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        TimeSpan delay,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");

        // Fire-and-forget: wait for the delay then write to channel.
        // The outer ValueTask completes immediately; the step becomes visible to the runner
        // only after 'delay' elapses or the cancellation token fires.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                await _writer.WriteAsync(new InMemoryStepEnvelope(context, flow, step, id), ct)
                             .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Host is stopping — let the envelope drop silently.
                // FlowRunRecoveryHostedService will re-enqueue on next startup.
            }
        }, ct);

        return new ValueTask<string?>(id);
    }
}
