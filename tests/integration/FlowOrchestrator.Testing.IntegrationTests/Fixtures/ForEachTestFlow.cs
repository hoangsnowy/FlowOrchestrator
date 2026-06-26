using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// ForEach test flow: <c>prepare</c> → <c>process_items</c> (a ForEach loop over
/// <c>@triggerBody()?.items</c>) → <c>finalize</c>. The loop child <c>iterate</c> runs once per
/// item; <c>finalize</c> runs after the loop step completes.
/// </summary>
/// <remarks>
/// Regression fixture for the dispatch-hint guard: a ForEach with a NON-empty collection must
/// fan out its runtime children (<c>process_items.{index}.iterate</c>) and still advance to the
/// downstream step. Previously every such child key tripped the "static DAG step" guard, so the
/// loop reported Succeeded while no child ran and the run hung forever.
/// </remarks>
public sealed class ForEachTestFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("22222222-2222-2222-2222-222222222222");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Trigger + step manifest with a single ForEach loop.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["prepare"] = new StepMetadata
            {
                Type = "Echo",
                Inputs = new Dictionary<string, object?> { ["label"] = "start" }
            },
            ["process_items"] = new LoopStepMetadata
            {
                Type = "ForEach",
                RunAfter = new RunAfterCollection { ["prepare"] = [StepStatus.Succeeded] },
                ForEach = "@triggerBody()?.items",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["iterate"] = new StepMetadata
                    {
                        Type = "Echo",
                        Inputs = new Dictionary<string, object?>()
                    }
                }
            },
            ["finalize"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["process_items"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "done" }
            }
        }
    };
}
