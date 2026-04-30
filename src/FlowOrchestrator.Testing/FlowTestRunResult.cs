using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Testing;

/// <summary>
/// Snapshot of a flow run returned by <see cref="FlowTestHost{TFlow}.TriggerAsync"/>.
/// Captures the run's terminal status, every step's outcome, and the recorded event log.
/// </summary>
public sealed class FlowTestRunResult
{
    /// <summary>The unique identifier the engine assigned to this run.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The run's terminal status — or <see cref="RunStatus.Running"/> if the test-host timeout fired before the run finished.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>Every step the engine recorded for this run, keyed by step key.</summary>
    public required IReadOnlyDictionary<string, FlowTestStepResult> Steps { get; init; }

    /// <summary>
    /// Persisted event log for this run, ordered by sequence ascending.
    /// Empty when <c>Observability.EnableEventPersistence</c> is left at its default (off).
    /// </summary>
    public required IReadOnlyList<FlowEventRecord> Events { get; init; }

    /// <summary>Total wall-clock time between <c>TriggerAsync</c> being called and the result being produced.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// <see langword="true"/> when the test-host's <c>timeout</c> parameter fired before the run reached a terminal state.
    /// Distinct from <see cref="RunStatus.TimedOut"/>, which represents a run-level timeout enforced by the engine.
    /// </summary>
    public required bool TimedOut { get; init; }

    /// <summary>
    /// Returns the number of attempts recorded for the given step key, or <c>0</c>
    /// when no step with that key was reached.
    /// </summary>
    /// <param name="stepKey">The manifest key of the step.</param>
    public int AttemptCount(string stepKey) =>
        Steps.TryGetValue(stepKey, out var step) ? step.AttemptCount : 0;
}
