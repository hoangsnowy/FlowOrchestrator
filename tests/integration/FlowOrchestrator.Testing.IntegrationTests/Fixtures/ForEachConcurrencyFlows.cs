using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Builds the manifest reported in issue #181: a ForEach whose body is a three-step sequence
/// (<c>robot_polish_start → wait_robot_polish → robot_polish_done</c>) with a
/// <c>WaitForSignal</c> in the middle, followed by a top-level step gated on the loop.
/// </summary>
/// <remarks>
/// The middle step is what makes the concurrency bound observable: a parked iteration stays live
/// indefinitely, so an implementation that merely staggers dispatch has every iteration waiting at
/// once. The two flows below differ only in <see cref="LoopStepMetadata.ConcurrencyLimit"/>.
/// </remarks>
internal static class ForEachConcurrencyManifest
{
    /// <summary>Signal name every iteration parks on, mirroring the issue's shared signal.</summary>
    public const string SignalName = "robot_polish_done";

    /// <summary>Builds the manifest with the given iteration concurrency bound.</summary>
    /// <param name="concurrencyLimit">Value assigned to the loop's <c>ConcurrencyLimit</c>.</param>
    public static FlowManifest Build(int concurrencyLimit) => new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["polish_start"] = new StepMetadata
            {
                Type = "Echo",
                Inputs = new Dictionary<string, object?> { ["label"] = "start" }
            },
            ["polish_process"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.Steps",
                ConcurrencyLimit = concurrencyLimit,
                RunAfter = new RunAfterCollection { ["polish_start"] = [StepStatus.Succeeded] },
                Steps = new StepCollection
                {
                    ["robot_polish_start"] = new StepMetadata
                    {
                        Type = "Echo",
                        Inputs = new Dictionary<string, object?> { ["label"] = "polishing" }
                    },
                    ["wait_robot_polish"] = new StepMetadata
                    {
                        Type = "WaitForSignal",
                        RunAfter = new RunAfterCollection { ["robot_polish_start"] = [StepStatus.Succeeded] },
                        Inputs = new Dictionary<string, object?> { ["signalName"] = SignalName }
                    },
                    ["robot_polish_done"] = new StepMetadata
                    {
                        Type = "Echo",
                        RunAfter = new RunAfterCollection { ["wait_robot_polish"] = [StepStatus.Succeeded] },
                        Inputs = new Dictionary<string, object?>
                        {
                            ["label"] = "@steps('wait_robot_polish').output.Point"
                        }
                    }
                }
            },
            ["polish_done"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["polish_process"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "done" }
            }
        }
    };
}

/// <summary>
/// Issue #181 verbatim: <c>ConcurrencyLimit = 1</c> must run the loop body one iteration at a
/// time, even though each iteration parks on a <c>WaitForSignal</c>.
/// </summary>
public sealed class ForEachSequentialSignalFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("81818181-8181-8181-8181-818181818181");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Trigger + step manifest with a sequential loop.</summary>
    public FlowManifest Manifest { get; set; } = ForEachConcurrencyManifest.Build(concurrencyLimit: 1);
}

/// <summary>
/// The other half of the contract: <c>ConcurrencyLimit = 2</c> must keep exactly two iterations in
/// flight — a gate that admitted one at a time regardless would pass the sequential test alone.
/// </summary>
public sealed class ForEachPairedSignalFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("82828282-8282-8282-8282-828282828282");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Trigger + step manifest with a two-at-a-time loop.</summary>
    public FlowManifest Manifest { get; set; } = ForEachConcurrencyManifest.Build(concurrencyLimit: 2);
}
