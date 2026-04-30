using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Single-step flow with a cron trigger that fires every minute.
/// The handler bumps <see cref="CronCallCounter.Increment"/> so tests can assert it ran.
/// </summary>
public sealed class CronFlow : IFlowDefinition
{
    public Guid Id { get; } = new("55555555-5555-5555-5555-555555555555");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["cron"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/1 * * * *" }
            }
        },
        Steps = new StepCollection
        {
            ["tick"] = new StepMetadata { Type = "CronTick" }
        }
    };
}

/// <summary>
/// Singleton call counter shared with <see cref="CronTickStepHandler"/>.
/// Exposes <see cref="FirstCall"/> so tests can <c>await</c> the first invocation
/// instead of polling on a wall-clock deadline (which is flaky on slow CI runners).
/// </summary>
public sealed class CronCallCounter
{
    private int _calls;
    private readonly TaskCompletionSource _firstCallSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Increments the counter and signals <see cref="FirstCall"/> on the first call.</summary>
    public int Increment()
    {
        var n = Interlocked.Increment(ref _calls);
        _firstCallSignal.TrySetResult();
        return n;
    }

    /// <summary>Total number of times the handler has been invoked.</summary>
    public int Calls => _calls;

    /// <summary>Completes the first time <see cref="Increment"/> is called.</summary>
    public Task FirstCall => _firstCallSignal.Task;
}

/// <summary>Handler that increments a shared counter — proves the cron trigger fired.</summary>
public sealed class CronTickStepHandler : IStepHandler
{
    private readonly CronCallCounter _counter;
    public CronTickStepHandler(CronCallCounter counter) => _counter = counter;

    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        _counter.Increment();
        return ValueTask.FromResult<object?>(new StepResult { Key = step.Key });
    }
}
