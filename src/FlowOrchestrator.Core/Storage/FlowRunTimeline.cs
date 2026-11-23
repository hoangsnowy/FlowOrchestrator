using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Derives a run's chronological timeline from data every store already keeps: the run record
/// and the per-execution rows of <see cref="FlowStepRecord.Attempts"/>.
/// </summary>
/// <remarks>
/// Replaces the persisted <c>FlowEvents</c> log removed in 2.0. That log cost two extra awaited
/// store commits per step on the execution path and duplicated what <c>FlowRuns</c> and
/// <c>FlowStepAttempts</c> already record, so the timeline is now rebuilt on read instead.
/// </remarks>
public static class FlowRunTimeline
{
    /// <summary>Entry type for the run's start.</summary>
    public const string RunStarted = "run.started";

    /// <summary>Entry type for the run reaching a terminal status.</summary>
    public const string RunCompleted = "run.completed";

    /// <summary>Entry type for an attempt starting to execute.</summary>
    public const string StepStarted = "step.started";

    /// <summary>Entry type for an attempt that finished with a non-failed terminal status.</summary>
    public const string StepCompleted = "step.completed";

    /// <summary>Entry type for an attempt that failed.</summary>
    public const string StepFailed = "step.failed";

    /// <summary>Entry type for a step that was skipped (unmet <c>runAfter</c>, a false <c>When</c> clause, or a terminated run).</summary>
    public const string StepSkipped = "step.skipped";

    /// <summary>Entry type for an attempt that ended non-terminally (a poll or signal wait that will run again, or a loop waiting on its iterations).</summary>
    public const string StepPending = "step.pending";

    /// <summary>
    /// Builds the timeline of <paramref name="run"/>: <c>run.started</c>, then per step attempt a
    /// <c>step.started</c> plus its outcome (<c>step.completed</c> / <c>step.failed</c> /
    /// <c>step.pending</c>), a single <c>step.skipped</c> for skipped steps, and finally
    /// <c>run.completed</c> once the run is terminal — ordered by timestamp.
    /// </summary>
    /// <param name="run">
    /// A run loaded with its steps, as returned by <see cref="IFlowRunStore.GetRunDetailAsync"/>.
    /// Steps without an attempt history are treated as a single attempt built from the step row.
    /// </param>
    /// <returns>The entries in chronological order, with <see cref="FlowEventRecord.Sequence"/> numbered from 1.</returns>
    /// <remarks>
    /// Complexity per call: O(a log a) time and O(a) allocation, where a is the number of attempt
    /// rows in the run (one sort over at most two entries per attempt plus the two run entries).
    /// Called on read only — never from the engine's step-completion path.
    /// <para>
    /// Ties on timestamp keep run.started first, run.completed last, and otherwise the per-attempt
    /// order (started before its outcome). A skipped step is reported by its skip entry only: a skip
    /// is recorded without the step having executed, so a preceding <c>step.started</c> would be
    /// misleading. Consecutive skipped attempts of one step are reported once.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<FlowEventRecord> Build(FlowRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var entries = new List<(FlowEventRecord Entry, int Rank, int Order)>(2 + ((run.Steps?.Count ?? 0) * 2));
        var order = 0;

        entries.Add((new FlowEventRecord
        {
            RunId = run.Id,
            Timestamp = run.StartedAt,
            Type = RunStarted,
            Message = string.IsNullOrEmpty(run.TriggerKey)
                ? "Flow run started."
                : $"Flow run started via trigger '{run.TriggerKey}'."
        }, 0, order++));

        if (run.Steps is not null)
        {
            foreach (var step in run.Steps)
            {
                if (step.Attempts is { Count: > 0 } attempts)
                {
                    var previousSkipped = false;
                    foreach (var attempt in attempts)
                    {
                        // A step already reported as skipped stays skipped: a parked step's safety-net
                        // invocation that lands after the run closed records another Skipped attempt,
                        // which would otherwise repeat the skip entry long after the run ended.
                        var skipped = IsSkipped(attempt.Status);
                        if (!(skipped && previousSkipped))
                        {
                            AddAttempt(entries, ref order, run.Id, step.StepKey, attempt.Status, attempt.StartedAt, attempt.CompletedAt, attempt.ErrorMessage);
                        }

                        previousSkipped = skipped;
                    }
                }
                else
                {
                    AddAttempt(entries, ref order, run.Id, step.StepKey, step.Status, step.StartedAt, step.CompletedAt, step.ErrorMessage);
                }
            }
        }

        if (run.CompletedAt is { } completedAt)
        {
            entries.Add((new FlowEventRecord
            {
                RunId = run.Id,
                Timestamp = completedAt,
                Type = RunCompleted,
                Message = $"Run completed with status {run.Status}."
            }, 2, order));   // last entry: nothing reads the counter after it
        }

        // Timestamp first; the rank pins run-level entries to the edges of a tie; the insertion
        // order keeps each attempt's started-before-outcome pair stable (List.Sort is unstable).
        entries.Sort(static (a, b) =>
        {
            var byTime = a.Entry.Timestamp.CompareTo(b.Entry.Timestamp);
            if (byTime != 0)
            {
                return byTime;
            }

            var byRank = a.Rank.CompareTo(b.Rank);
            return byRank != 0 ? byRank : a.Order.CompareTo(b.Order);
        });

        var result = new FlowEventRecord[entries.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var entry = entries[i].Entry;
            entry.Sequence = i + 1;
            result[i] = entry;
        }

        return result;
    }

    private static bool IsSkipped(string? status) =>
        string.Equals(status, nameof(StepStatus.Skipped), StringComparison.Ordinal);

    private static void AddAttempt(
        List<(FlowEventRecord Entry, int Rank, int Order)> entries,
        ref int order,
        Guid runId,
        string stepKey,
        string? status,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        string? errorMessage)
    {
        if (IsSkipped(status))
        {
            entries.Add((new FlowEventRecord
            {
                RunId = runId,
                Timestamp = completedAt ?? startedAt,
                Type = StepSkipped,
                StepKey = stepKey,
                Message = errorMessage ?? $"Step '{stepKey}' skipped."
            }, 1, order++));
            return;
        }

        entries.Add((new FlowEventRecord
        {
            RunId = runId,
            Timestamp = startedAt,
            Type = StepStarted,
            StepKey = stepKey,
            Message = $"Step '{stepKey}' started."
        }, 1, order++));

        if (completedAt is not { } finishedAt)
        {
            return;
        }

        var (type, message) = status switch
        {
            nameof(StepStatus.Failed) => (StepFailed, errorMessage ?? $"Step '{stepKey}' failed."),
            nameof(StepStatus.Pending) => (StepPending, $"Step '{stepKey}' pending; it will run again."),
            nameof(StepStatus.Running) => (StepPending, $"Step '{stepKey}' is waiting for its child steps to complete."),
            _ => (StepCompleted, $"Step '{stepKey}' completed with status {status}.")
        };

        entries.Add((new FlowEventRecord
        {
            RunId = runId,
            Timestamp = finishedAt,
            Type = type,
            StepKey = stepKey,
            Message = message
        }, 1, order++));
    }
}
