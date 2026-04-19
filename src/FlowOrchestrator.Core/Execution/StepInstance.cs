namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Default mutable implementation of <see cref="IStepInstance"/>, constructed by
/// <see cref="FlowExecutor"/> and <see cref="FlowGraphPlanner"/> when a step is ready to run.
/// </summary>
public sealed class StepInstance : IStepInstance
{
    /// <summary>Initialises a step instance with its manifest key and handler type name.</summary>
    /// <param name="key">The step's key in the flow manifest (or its runtime loop path).</param>
    /// <param name="type">The type name used to look up the registered handler.</param>
    public StepInstance(string key, string type)
    {
        Key = key;
        Type = type;
    }

    /// <inheritdoc/>
    public Guid RunId { get; set; }

    /// <inheritdoc/>
    public string? PrincipalId { get; set; }

    /// <inheritdoc/>
    public object? TriggerData { get; set; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }

    /// <inheritdoc/>
    public DateTimeOffset ScheduledTime { get; set; }

    /// <inheritdoc/>
    public string Type { get; set; }

    /// <inheritdoc/>
    public string Key { get; }

    /// <inheritdoc/>
    public IDictionary<string, object?> Inputs { get; set; } = new Dictionary<string, object?>();

    /// <inheritdoc/>
    public int Index { get; set; }

    /// <inheritdoc/>
    public bool ScopeMoveNext { get; set; }
}
