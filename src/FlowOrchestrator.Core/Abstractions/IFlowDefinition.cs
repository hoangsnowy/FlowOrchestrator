namespace FlowOrchestrator.Core.Abstractions;

public interface IFlowDefinition
{
    Guid Id { get; }
    string Version { get; }
    FlowManifest Manifest { get; set; }
}
