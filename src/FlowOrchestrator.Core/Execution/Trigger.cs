namespace FlowOrchestrator.Core.Execution;

public sealed class Trigger : ITrigger
{
    public Trigger(string key, string type, object? data)
    {
        Key = key;
        Type = type;
        Data = data;
    }

    public string Key { get; }
    public string Type { get; }
    public object? Data { get; }
}
