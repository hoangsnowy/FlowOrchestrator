namespace FlowOrchestrator.Core.Execution;

public sealed class StepInstance : IStepInstance
{
    public StepInstance(string key, string type)
    {
        Key = key;
        Type = type;
    }

    public Guid RunId { get; set; }
    public string? PrincipalId { get; set; }
    public object? TriggerData { get; set; }
    public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
    public DateTimeOffset ScheduledTime { get; set; }
    public string Type { get; set; }
    public string Key { get; }
    public IDictionary<string, object?> Inputs { get; set; } = new Dictionary<string, object?>();
    public int Index { get; set; }
    public bool ScopeMoveNext { get; set; }
}
