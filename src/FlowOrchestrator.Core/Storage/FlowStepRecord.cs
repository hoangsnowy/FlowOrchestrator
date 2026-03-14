namespace FlowOrchestrator.Core.Storage;

public sealed class FlowStepRecord
{
    public Guid RunId { get; set; }
    public string StepKey { get; set; } = default!;
    public string StepType { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }
    public string? ErrorMessage { get; set; }
    public string? JobId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
