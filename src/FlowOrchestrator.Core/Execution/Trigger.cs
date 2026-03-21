namespace FlowOrchestrator.Core.Execution;

public sealed class Trigger : ITrigger
{
    public Trigger(string key, string type, object? data, IReadOnlyDictionary<string, string>? headers = null)
    {
        Key = key;
        Type = type;
        Data = data;
        Headers = headers;
    }

    public string Key { get; }
    public string Type { get; }
    public object? Data { get; }
    public IReadOnlyDictionary<string, string>? Headers { get; }
}
