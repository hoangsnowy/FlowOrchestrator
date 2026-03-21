namespace FlowOrchestrator.Core.Storage;

public sealed class FlowRunRecord
{
    public Guid Id { get; set; }
    public Guid FlowId { get; set; }
    public string? FlowName { get; set; }
    public string Status { get; set; } = default!;
    public string? TriggerKey { get; set; }
    public string? TriggerDataJson { get; set; }
    public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
    public string? BackgroundJobId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public IReadOnlyList<FlowStepRecord>? Steps { get; set; }
}
