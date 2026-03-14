namespace FlowOrchestrator.Core.Abstractions;

public interface IScopedStep
{
    StepCollection Steps { get; set; }
    string Type { get; set; }
}
