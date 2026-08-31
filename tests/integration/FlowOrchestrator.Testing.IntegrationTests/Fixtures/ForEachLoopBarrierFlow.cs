using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Replicates the manifest reported in issue #169: a ForEach loop (<c>scan_process</c>) whose
/// body parks on a <c>WaitForSignal</c> child, followed by a top-level step
/// (<c>robot_callback_success</c>) that declares <c>RunAfter = { scan_process: [Succeeded] }</c>.
/// </summary>
/// <remarks>
/// The reporter observed <c>robot_callback_success</c> executing <b>before</b> the loop body,
/// because the loop step reported <see cref="StepStatus.Succeeded"/> the moment it fanned out.
/// With the loop barrier the loop stays <see cref="StepStatus.Running"/> until every iteration
/// is terminal, so the downstream step can only run last. <c>ConcurrencyLimit = 1</c> and the
/// shared signal name (<c>robot_goto</c>) mirror the report verbatim.
/// </remarks>
public sealed class ForEachLoopBarrierFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("55555555-5555-5555-5555-555555555555");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Trigger + step manifest mirroring the issue #169 configuration.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["scan_process"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.Steps",
                ConcurrencyLimit = 1,
                Steps = new StepCollection
                {
                    ["wait_robot_goto"] = new StepMetadata
                    {
                        Type = "WaitForSignal",
                        Inputs = new Dictionary<string, object?>
                        {
                            ["signalName"] = "robot_goto"
                        }
                    },
                    ["open_camera"] = new StepMetadata
                    {
                        Type = "Echo",
                        RunAfter = new RunAfterCollection { ["wait_robot_goto"] = [StepStatus.Succeeded] },
                        Inputs = new Dictionary<string, object?>
                        {
                            ["label"] = "@steps('wait_robot_goto').output.Location"
                        }
                    }
                }
            },
            ["robot_callback_success"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["scan_process"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "callback" }
            }
        }
    };
}
