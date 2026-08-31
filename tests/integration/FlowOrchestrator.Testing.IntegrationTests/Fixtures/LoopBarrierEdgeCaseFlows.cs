using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Nested loops: an outer <c>ForEach</c> whose body is another <c>ForEach</c>, followed by a
/// top-level step gated on the outer loop. Proves the inner barrier settles before the outer one
/// and that the downstream step waits for the whole two-level fan-out.
/// </summary>
public sealed class NestedForEachFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("66666666-6666-6666-6666-666666666661");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Outer loop over the trigger payload, inner loop over a static pair.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["outer"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.groups",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["inner"] = new LoopStepMetadata
                    {
                        Type = "ForEach",
                        ForEach = new List<object?> { "x", "y" },
                        ConcurrencyLimit = 2,
                        Steps = new StepCollection
                        {
                            ["leaf"] = new StepMetadata
                            {
                                Type = "Echo",
                                Inputs = new Dictionary<string, object?> { ["label"] = "leaf" }
                            }
                        }
                    }
                }
            },
            ["after_outer"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["outer"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "after" }
            }
        }
    };
}

/// <summary>
/// A loop whose first child always throws, blocking its sibling. Locks the documented semantics:
/// a failed iteration still settles the barrier (`Failed` and `Skipped` are terminal), so the
/// step gated on the loop still runs — only its timing changed.
/// </summary>
public sealed class ForEachFailingChildFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("66666666-6666-6666-6666-666666666662");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Loop body pairs a throwing step with a dependent that can never run.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.items",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["boom"] = new StepMetadata { Type = "Boom" },
                    ["never"] = new StepMetadata
                    {
                        Type = "Echo",
                        RunAfter = new RunAfterCollection { ["boom"] = [StepStatus.Succeeded] },
                        Inputs = new Dictionary<string, object?> { ["label"] = "never" }
                    }
                }
            },
            ["after_loop"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "after" }
            }
        }
    };
}

/// <summary>
/// A loop whose second child carries a <c>When</c> clause that evaluates to <see langword="false"/>.
/// The engine records it <see cref="StepStatus.Skipped"/>, which must count as terminal for the
/// barrier — otherwise a `When`-gated loop body would park the loop forever.
/// </summary>
public sealed class ForEachWhenSkipFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("66666666-6666-6666-6666-666666666663");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Loop body whose consumer is gated on a false trigger-body condition.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.items",
                ConcurrencyLimit = 1,
                Steps = new StepCollection
                {
                    ["first"] = new StepMetadata
                    {
                        Type = "Echo",
                        Inputs = new Dictionary<string, object?> { ["label"] = "first" }
                    },
                    ["gated"] = new StepMetadata
                    {
                        Type = "Echo",
                        RunAfter = new RunAfterCollection
                        {
                            ["first"] = new RunAfterCondition
                            {
                                Statuses = [StepStatus.Succeeded],
                                When = "@triggerBody().amount > 1000"
                            }
                        },
                        Inputs = new Dictionary<string, object?> { ["label"] = "gated" }
                    }
                }
            },
            ["after_loop"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "after" }
            }
        }
    };
}

/// <summary>
/// A loop body that parks on a <c>WaitForSignal</c> with a short timeout and is never signalled.
/// The waiter fails on expiry, which must settle the barrier rather than strand the run.
/// </summary>
public sealed class ForEachSignalTimeoutFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("66666666-6666-6666-6666-666666666664");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Loop body waiting on a signal nobody sends.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.items",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["wait"] = new StepMetadata
                    {
                        Type = "WaitForSignal",
                        Inputs = new Dictionary<string, object?>
                        {
                            ["signalName"] = "never_sent",
                            ["timeoutSeconds"] = 1
                        }
                    }
                }
            },
            ["after_loop"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "after" }
            }
        }
    };
}

/// <summary>
/// Two loops chained by <c>RunAfter</c>. The second loop must not fan out until the first one's
/// barrier settled, and the tail step must run after both.
/// </summary>
public sealed class SequentialForEachFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("66666666-6666-6666-6666-666666666665");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>`loop_a` → `loop_b` → `tail`.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["loop_a"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.items",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["work_a"] = new StepMetadata
                    {
                        Type = "Echo",
                        Inputs = new Dictionary<string, object?> { ["label"] = "a" }
                    }
                }
            },
            ["loop_b"] = new LoopStepMetadata
            {
                Type = "ForEach",
                RunAfter = new RunAfterCollection { ["loop_a"] = [StepStatus.Succeeded] },
                ForEach = "@triggerBody()?.items",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["work_b"] = new StepMetadata
                    {
                        Type = "Echo",
                        Inputs = new Dictionary<string, object?> { ["label"] = "b" }
                    }
                }
            },
            ["tail"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["loop_b"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "tail" }
            }
        }
    };
}
