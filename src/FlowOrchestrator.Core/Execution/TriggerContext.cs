using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default mutable implementation of <see cref="ITriggerContext"/>.
/// Constructed by <c>HangfireFlowOrchestrator.TriggerAsync</c> before execution begins.
/// </summary>
public sealed class TriggerContext : ITriggerContext
{
    /// <inheritdoc/>
    public Guid RunId { get; set; }

    /// <inheritdoc/>
    public string? PrincipalId { get; set; }

    /// <inheritdoc/>
    public object? TriggerData { get; set; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }

    /// <inheritdoc/>
    public IFlowDefinition Flow { get; set; } = default!;

    /// <inheritdoc/>
    public ITrigger Trigger { get; set; } = default!;

    /// <inheritdoc/>
    public Guid? SourceRunId { get; set; }

    /// <inheritdoc/>
    public string? JobId { get; set; }
}
