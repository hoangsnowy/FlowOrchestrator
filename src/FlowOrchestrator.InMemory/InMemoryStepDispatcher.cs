using System.Diagnostics;
using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;

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
    private const string RuntimeName = "in_memory";

    private readonly ChannelWriter<InMemoryStepEnvelope> _writer;
    private readonly FlowOrchestratorTelemetry? _telemetry;

    /// <summary>Initialises the dispatcher with the shared channel writer.</summary>
    /// <param name="writer">Writer side of the channel the runner drains.</param>
    /// <param name="telemetry">Optional — when supplied, each channel write is timed on <c>flow_step_dispatch_duration_ms</c>.</param>
    /// <param name="observability">Optional — <see cref="FlowObservabilityOptions.EnableOpenTelemetry"/> gates the timing.</param>
    /// <remarks>
    /// Only <see cref="EnqueueStepAsync"/> is timed. <see cref="ScheduleStepAsync"/> returns before
    /// doing any work — the write happens after the delay, off the caller's path — so there is no
    /// dispatch cost for the histogram to attribute.
    /// </remarks>
    public InMemoryStepDispatcher(
        ChannelWriter<InMemoryStepEnvelope> writer,
        FlowOrchestratorTelemetry? telemetry = null,
        FlowObservabilityOptions? observability = null)
    {
        _writer = writer;
        _telemetry = observability?.EnableOpenTelemetry == false ? null : telemetry;
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
        var start = Stopwatch.GetTimestamp();
        var ok = false;
        try
        {
            await _writer.WriteAsync(new InMemoryStepEnvelope(context, flow, step, id) { ParentTraceContext = traceContext }, ct).ConfigureAwait(false);
            ok = true;
        }
        finally
        {
            _telemetry?.RecordDispatch(RuntimeName, scheduled: false, ok, start);
        }

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
