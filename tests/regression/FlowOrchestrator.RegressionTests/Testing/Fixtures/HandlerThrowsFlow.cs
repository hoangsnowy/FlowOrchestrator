using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// One-step manual flow whose handler throws on the first N attempts and succeeds afterwards.
/// Used by the manual-retry regression test to verify that <see cref="IFlowOrchestrator.RetryStepAsync"/>
/// resets per-attempt state correctly across multiple failure / retry cycles.
/// </summary>
public sealed class HandlerThrowsFlow : IFlowDefinition
{
    public Guid Id { get; } = new("cccccccc-3333-3333-3333-cccccccccccc");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["flaky"] = new StepMetadata { Type = "Flaky" }
        }
    };
}

/// <summary>
/// Configurable failure budget: throws on the first <see cref="FailUntilAttempt"/> calls,
/// then returns success. Tests use it to drive deterministic retry sequences.
/// </summary>
public sealed class FlakyHandlerProbe
{
    private int _attempt;
    public int Attempt => _attempt;
    public int FailUntilAttempt { get; set; }
    public int RecordAttempt() => Interlocked.Increment(ref _attempt);
}

/// <summary>Throws on the first N attempts driven by the injected probe; succeeds afterwards.</summary>
public sealed class FlakyStepHandler : IStepHandler
{
    private readonly FlakyHandlerProbe _probe;
    public FlakyStepHandler(FlakyHandlerProbe probe) => _probe = probe;

    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        var attempt = _probe.RecordAttempt();
        if (attempt <= _probe.FailUntilAttempt)
        {
            throw new InvalidOperationException($"Simulated failure on attempt {attempt}.");
        }
        return ValueTask.FromResult<object?>(new StepResult { Key = step.Key });
    }
}

/// <summary>
/// One-step flow whose only step references a malformed trigger expression.
/// Used by <c>TriggerBodyExpressionErrorHandlingTests</c> to verify that the engine
/// surfaces resolution errors as a Failed run rather than hanging.
/// </summary>
public sealed class MalformedExpressionFlow : IFlowDefinition
{
    public Guid Id { get; } = new("dddddddd-4444-4444-4444-dddddddddddd");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["read"] = new StepMetadata
            {
                Type = "EchoInput",
                Inputs = new Dictionary<string, object?>
                {
                    // Trigger body is null at runtime — '?.' chain returns null instead of throwing
                    // and the handler observes a null input. The test asserts the run completes
                    // (no hang, no NRE) and the step doesn't see a corrupt resolved value.
                    ["value"] = "@triggerBody()?.does.not.exist"
                }
            }
        }
    };
}

/// <summary>Echo handler: returns its single string input verbatim so tests can assert what was resolved.</summary>
public sealed class EchoInput
{
    public string? Value { get; set; }
}

/// <summary>Captures the resolved value into a probe so the test can inspect what the engine produced.</summary>
public sealed class ExpressionEchoProbe
{
    public string? LastResolvedValue { get; set; }
    public int Calls;
    public int Increment() => Interlocked.Increment(ref Calls);
}

/// <summary>Records the resolved input into a shared probe.</summary>
public sealed class EchoInputStepHandler : IStepHandler<EchoInput>
{
    private readonly ExpressionEchoProbe _probe;
    public EchoInputStepHandler(ExpressionEchoProbe probe) => _probe = probe;

    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance<EchoInput> step)
    {
        _probe.Increment();
        _probe.LastResolvedValue = step.Inputs?.Value;
        return ValueTask.FromResult<object?>(new StepResult { Key = step.Key });
    }
}
