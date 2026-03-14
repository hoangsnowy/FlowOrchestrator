using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

public sealed class HelloWorldFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000001");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = "Manual" }
        },
        Steps = new StepCollection
        {
            ["step1"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?> { ["message"] = "Hello from step 1!" }
            },
            ["step2"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["step1"] = ["Succeeded"] },
                Inputs = new Dictionary<string, object?> { ["message"] = "Hello from step 2 – workflow complete!" }
            }
        }
    };
}
