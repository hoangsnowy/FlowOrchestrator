using System.Diagnostics;
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
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and then write, so <see cref="ScheduleStepAsync"/> returns
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
        var traceContext = CaptureCurrentTraceContext();
        await _writer.WriteAsync(new InMemoryStepEnvelope(context, flow, step, id) { ParentTraceContext = traceContext }, ct).ConfigureAwait(false);
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
        var traceContext = CaptureCurrentTraceContext();

        // Fire-and-forget: wait for the delay then write to channel.
        // The outer ValueTask completes immediately; the step becomes visible to the runner
        // only after 'delay' elapses.
        //
        // We deliberately DROP the caller's `ct` for the deferred write. The dispatcher is often
        // invoked from a per-request scope (signal endpoint, webhook handler, dashboard rerun),
        // and that ct is cancelled the moment the HTTP response flushes — passing it to the
        // background Task.Delay would silently kill the schedule (observed in v1.26.1 as
        // WaitForSignal never resuming on the InMemory runtime). Host shutdown still tears
        // down the channel, which makes WriteAsync throw — that path is caught below and
        // FlowRunRecoveryHostedService re-enqueues on next startup.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
                await _writer.WriteAsync(
                                  new InMemoryStepEnvelope(context, flow, step, id) { ParentTraceContext = traceContext },
                                  CancellationToken.None)
                             .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Host is stopping; recovery hosted service will re-enqueue on next startup.
            }
        });

        return new ValueTask<string?>(id);
    }

    /// <summary>
    /// Captures <see cref="Activity.Current"/>'s context if it is W3C-formatted; the runner
    /// uses it to re-parent the step's span and keep the distributed trace continuous across
    /// the channel handover.
    /// </summary>
    private static ActivityContext? CaptureCurrentTraceContext()
    {
        var current = Activity.Current;
        return current is { IdFormat: ActivityIdFormat.W3C } ? current.Context : null;
    }
}
