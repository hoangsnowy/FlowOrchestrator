namespace FlowOrchestrator.Core.Storage;

public sealed class FlowEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Type { get; init; } = default!;
    public string? StepKey { get; init; }
    public string? Message { get; init; }
}
