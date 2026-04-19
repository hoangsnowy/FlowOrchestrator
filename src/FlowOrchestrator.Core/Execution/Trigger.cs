namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Immutable <see cref="ITrigger"/> implementation constructed at trigger time
/// by <c>HangfireFlowOrchestrator.TriggerAsync</c>.
/// </summary>
public sealed class Trigger : ITrigger
{
    /// <summary>Initialises a trigger with the given key, type, payload, and optional HTTP headers.</summary>
    public Trigger(string key, string type, object? data, IReadOnlyDictionary<string, string>? headers = null)
    {
        Key = key;
        Type = type;
        Data = data;
        Headers = headers;
    }

    /// <inheritdoc/>
    public string Key { get; }

    /// <inheritdoc/>
    public string Type { get; }

    /// <inheritdoc/>
    public object? Data { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string>? Headers { get; }
}
