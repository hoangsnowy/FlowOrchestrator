using System.Diagnostics;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Testing.Internal;

/// <summary>
/// Polls <see cref="IFlowRunStore"/> at a fixed cadence until a run reaches a terminal status
/// or the supplied timeout elapses.
/// </summary>
internal static class RunPoller
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public static async Task<FlowTestRunResult> WaitForTerminalAsync(
        IFlowRunStore runStore,
        IFlowEventReader? eventReader,
        Guid runId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var deadline = sw.Elapsed + timeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var run = await runStore.GetRunDetailAsync(runId).ConfigureAwait(false);
            if (run is not null && ResultMapper.IsTerminal(run.Status))
            {
                var events = eventReader is null
                    ? Array.Empty<FlowEventRecord>()
                    : await eventReader.GetRunEventsAsync(runId).ConfigureAwait(false);
                return ResultMapper.Map(run, events, sw.Elapsed, timedOut: false);
            }

            if (sw.Elapsed >= deadline)
            {
                var snapshot = run ?? new FlowRunRecord
                {
                    Id = runId,
                    Status = "Running",
                    StartedAt = DateTimeOffset.UtcNow
                };
                var events = eventReader is null
                    ? Array.Empty<FlowEventRecord>()
                    : await eventReader.GetRunEventsAsync(runId).ConfigureAwait(false);
                return ResultMapper.Map(snapshot, events, sw.Elapsed, timedOut: true);
            }

            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Delay was cancelled by the deadline-driven token; loop will exit on next iteration.
            }
        }
    }
}
