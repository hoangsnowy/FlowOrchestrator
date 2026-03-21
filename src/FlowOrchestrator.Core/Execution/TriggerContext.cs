using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Execution;

public sealed class TriggerContext : ITriggerContext
{
    public Guid RunId { get; set; }
    public string? PrincipalId { get; set; }
    public object? TriggerData { get; set; }
    public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
    public IFlowDefinition Flow { get; set; } = default!;
    public ITrigger Trigger { get; set; } = default!;
    public string? JobId { get; set; }
}
