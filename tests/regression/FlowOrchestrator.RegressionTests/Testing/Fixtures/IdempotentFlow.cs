using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Single-step webhook flow used by <see cref="WebhookIdempotencyTests"/> to assert that
/// the engine de-duplicates triggers carrying the same <c>Idempotency-Key</c> header.
/// </summary>
public sealed class IdempotentFlow : IFlowDefinition
{
    public Guid Id { get; } = new("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?> { ["webhookSlug"] = "webhook" }
            }
        },
        Steps = new StepCollection
        {
            ["count"] = new StepMetadata { Type = "CountInvocations" }
        }
    };
}

/// <summary>Singleton counter shared between handler invocations across the same test process.</summary>
public sealed class IdempotencyInvocationCounter
{
    private int _calls;
    public int Calls => _calls;
    public int Increment() => Interlocked.Increment(ref _calls);
}

/// <summary>Handler that bumps the shared counter so the test can assert the engine ran the step exactly once.</summary>
public sealed class CountInvocationsStepHandler : IStepHandler
{
    private readonly IdempotencyInvocationCounter _counter;
    public CountInvocationsStepHandler(IdempotencyInvocationCounter counter) => _counter = counter;

    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        _counter.Increment();
        return ValueTask.FromResult<object?>(new StepResult { Key = step.Key });
    }
}
