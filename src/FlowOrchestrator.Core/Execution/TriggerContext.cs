using System.Runtime.Serialization;
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
    /// <remarks>
    /// Marked <see cref="IgnoreDataMemberAttribute"/> so Newtonsoft.Json (Hangfire's job serialiser) neither
    /// writes nor reads it. FlowOrchestrator 1.17 – 1.34.x enqueued Hangfire step jobs with this context as an
    /// argument, so those payloads embed the whole flow manifest, whose <see cref="RunAfterCondition"/> values
    /// Newtonsoft can no longer read since Core dropped its Newtonsoft converter. The step-job overload
    /// resolves the flow from <see cref="FlowRegistry"/> by id and never reads this property, so skipping it
    /// keeps those queued jobs runnable on 2.0. System.Text.Json ignores the attribute.
    /// </remarks>
    [IgnoreDataMember]
    public IFlowDefinition Flow { get; set; } = default!;

    /// <inheritdoc/>
    public ITrigger Trigger { get; set; } = default!;

    /// <inheritdoc/>
    public Guid? SourceRunId { get; set; }

    /// <inheritdoc/>
    public string? JobId { get; set; }
}
