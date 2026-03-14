namespace FlowOrchestrator.Core.Execution;

public interface ITrigger
{
    string Key { get; }
    string Type { get; }
    object? Data { get; }
}
