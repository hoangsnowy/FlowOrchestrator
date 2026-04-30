using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>Single-step flow whose handler awaits a long delay — used to verify TriggerAsync timeout behaviour.</summary>
public sealed class SlowFlow : IFlowDefinition
{
    public Guid Id { get; } = new("77777777-7777-7777-7777-777777777777");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["slow"] = new StepMetadata { Type = "Slow" }
        }
    };
}

/// <summary>Handler that awaits 5 seconds — far longer than the test-host timeout under test.</summary>
public sealed class SlowStepHandler : IStepHandler
{
    public async ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        return new StepResult { Key = step.Key };
    }
}
