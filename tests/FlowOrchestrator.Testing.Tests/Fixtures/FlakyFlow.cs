using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>Single-step flow whose handler throws on the first call but succeeds on subsequent calls.</summary>
public sealed class FlakyFlow : IFlowDefinition
{
    public Guid Id { get; } = new("33333333-3333-3333-3333-333333333333");
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
/// Handler that fails on attempt 1 and succeeds afterwards. Counter is shared across handler instances
/// because it lives on the singleton <see cref="FlakyCounter"/> service.
/// </summary>
public sealed class FlakyStepHandler : IStepHandler
{
    private readonly FlakyCounter _counter;
    public FlakyStepHandler(FlakyCounter counter) => _counter = counter;

    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        var attempt = _counter.Increment();
        if (attempt == 1)
        {
            throw new InvalidOperationException("transient failure");
        }
        return ValueTask.FromResult<object?>(new StepResult { Key = step.Key });
    }
}

/// <summary>Singleton counter shared with <see cref="FlakyStepHandler"/> to drive deterministic flake-then-pass behaviour.</summary>
public sealed class FlakyCounter
{
    private int _calls;
    public int Increment() => Interlocked.Increment(ref _calls);
    public int Calls => _calls;
}
