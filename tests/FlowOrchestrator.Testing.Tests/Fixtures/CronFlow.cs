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

/// <summary>Singleton call counter shared with <see cref="CronTickStepHandler"/>.</summary>
public sealed class CronCallCounter
{
    private int _calls;
    public int Increment() => Interlocked.Increment(ref _calls);
    public int Calls => _calls;
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
