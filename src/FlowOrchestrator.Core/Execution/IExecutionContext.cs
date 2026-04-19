namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Carries the ambient state for a single flow run, shared across all steps in that run.
/// Populated once by <c>TriggerAsync</c> and propagated via <see cref="IExecutionContextAccessor"/>.
/// </summary>
public interface IExecutionContext
{
    /// <summary>Unique identifier for this flow run, generated at trigger time.</summary>
    Guid RunId { get; set; }

    /// <summary>
    /// Identity of the caller that triggered the run, or <see langword="null"/> if anonymous.
    /// Sourced from the authentication principal at trigger time.
    /// </summary>
    string? PrincipalId { get; set; }

    /// <summary>
    /// Deserialized body of the trigger payload (e.g. the webhook request body or manual trigger data).
    /// Available to steps via <c>@triggerBody()</c> expressions.
    /// </summary>
    object? TriggerData { get; set; }

    /// <summary>
    /// HTTP headers from the inbound trigger request, or <see langword="null"/> for non-HTTP triggers.
    /// Available to steps via <c>@triggerHeaders()</c> expressions.
    /// </summary>
    IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
}
