using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// ForEach flow whose loop body has two sibling child steps where the second reads the
/// first's output by <b>bare key</b>: <c>emit</c> → <c>consume</c>, and
/// <c>consume.label = @steps('emit').output.marker</c>.
/// </summary>
/// <remarks>
/// Regression fixture for issue #166: a nested ForEach child referencing a sibling child's
/// output via <c>@steps('sibling')</c> must resolve to the current iteration's runtime key
/// (<c>process_items.{index}.emit</c>) instead of throwing
/// "Step 'emit' is not defined in the flow manifest".
/// </remarks>
public sealed class ForEachSiblingReferenceFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("33333333-3333-3333-3333-333333333333");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Trigger + step manifest with a two-step ForEach body using a sibling output reference.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["process_items"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.items",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["emit"] = new StepMetadata
                    {
                        Type = "EmitIndex",
                        Inputs = new Dictionary<string, object?>()
                    },
                    ["consume"] = new StepMetadata
                    {
                        Type = "Echo",
                        RunAfter = new RunAfterCollection { ["emit"] = [StepStatus.Succeeded] },
                        Inputs = new Dictionary<string, object?>
                        {
                            // Bare sibling reference — must resolve to process_items.{index}.emit.
                            ["label"] = "@steps('emit').output.marker"
                        }
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

/// <summary>Output of <see cref="EmitIndexStepHandler"/>: a per-iteration marker.</summary>
public sealed class EmitIndexOutput
{
    /// <summary>A stable, iteration-specific marker of the form <c>iter-{index}</c>.</summary>
    public string? Marker { get; set; }
}

/// <summary>
/// Test handler that emits a marker derived from the injected <c>__loopIndex</c>, giving each
/// loop iteration a distinct output so a sibling reference can be proven to read the correct one.
/// </summary>
public sealed class EmitIndexStepHandler : IStepHandler
{
    /// <inheritdoc/>
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        var index = step.Inputs.TryGetValue("__loopIndex", out var raw) ? Convert.ToInt32(raw) : -1;
        return ValueTask.FromResult<object?>(new StepResult<EmitIndexOutput>
        {
            Key = step.Key,
            Value = new EmitIndexOutput { Marker = $"iter-{index}" }
        });
    }
}
