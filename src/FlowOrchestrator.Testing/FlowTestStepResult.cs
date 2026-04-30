using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Testing;

/// <summary>
/// Snapshot of a single step's state at the moment a flow run reached a terminal state
/// (or the test-host's wait window elapsed). Returned on <see cref="FlowTestRunResult.Steps"/>.
/// </summary>
public sealed class FlowTestStepResult
{
    /// <summary>The step's terminal status.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>
    /// Step output parsed as a <see cref="JsonElement"/>. Returns a JSON <see langword="null"/> element
    /// when the step did not produce an output (e.g. failed before completing).
    /// </summary>
    public required JsonElement Output { get; init; }

    /// <summary>
    /// Step inputs as resolved at execution time, parsed as <see cref="JsonElement"/>.
    /// Useful for asserting that <c>@triggerBody()</c>/<c>@triggerHeaders()</c> expressions resolved correctly.
    /// </summary>
    public required JsonElement Inputs { get; init; }

    /// <summary>Failure reason captured by the engine when <see cref="Status"/> is <see cref="StepStatus.Failed"/>.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Total number of attempts the engine made for this step (including retries).</summary>
    public required int AttemptCount { get; init; }

    /// <summary>Wall-clock time when the step first started executing, in UTC.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Wall-clock time when the step reached its terminal status, in UTC.</summary>
    public DateTimeOffset? CompletedAt { get; init; }
}
