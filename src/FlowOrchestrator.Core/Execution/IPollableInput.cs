namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Marks a step input POCO as supporting the polling pattern.
/// When implemented, the <see cref="PollableStepHandler{TInput}"/> base class manages
/// retry scheduling, timeout enforcement, and condition evaluation automatically.
/// </summary>
public interface IPollableInput
{
    /// <summary>
    /// When <see langword="true"/>, the base class checks the poll condition and reschedules
    /// via Hangfire delay until it is met or the timeout expires.
    /// When <see langword="false"/>, the handler executes exactly once regardless of the result.
    /// </summary>
    bool PollEnabled { get; }

    /// <summary>Seconds to wait between poll attempts. Minimum effective value is <c>1</c>.</summary>
    int PollIntervalSeconds { get; }

    /// <summary>
    /// Maximum total seconds to keep polling before the step is marked <see cref="StepStatus.Failed"/>.
    /// Must be greater than or equal to <see cref="PollIntervalSeconds"/>.
    /// </summary>
    int PollTimeoutSeconds { get; }

    /// <summary>
    /// Minimum number of successful fetch attempts required before the condition is evaluated.
    /// Prevents false positives on the very first response.
    /// </summary>
    int PollMinAttempts { get; }

    /// <summary>
    /// Dot-notation JSON path evaluated against the fetch result to locate the value to compare
    /// (e.g. <c>"status"</c> or <c>"result.state"</c>).
    /// When <see langword="null"/>, the full payload is tested for non-empty content.
    /// </summary>
    string? PollConditionPath { get; }

    /// <summary>
    /// The expected value at <see cref="PollConditionPath"/>. Comparison is case-insensitive string equality.
    /// When <see langword="null"/>, the condition succeeds as soon as the path resolves to a non-empty value.
    /// </summary>
    object? PollConditionEquals { get; }

    /// <summary>
    /// ISO 8601 timestamp recorded on the first poll attempt; used to track total elapsed time.
    /// Managed by the base class — do not set this manually.
    /// </summary>
    string? PollStartedAtUtc { get; set; }

    /// <summary>
    /// Running count of poll attempts for this step invocation.
    /// Managed by the base class — do not set this manually.
    /// </summary>
    int? PollAttempt { get; set; }
}
