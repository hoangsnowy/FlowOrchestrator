using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>Single-step flow whose handler resolves <see cref="IGreeter"/> from DI to verify <c>WithService</c> wiring.</summary>
public sealed class ServiceConsumerFlow : IFlowDefinition
{
    public Guid Id { get; } = new("88888888-8888-8888-8888-888888888888");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["greet"] = new StepMetadata { Type = "Greet" }
        }
    };
}

/// <summary>Greeter contract injected into the handler under test.</summary>
public interface IGreeter
{
    string Greet(string name);
}

/// <summary>Test fake returning a deterministic greeting.</summary>
public sealed class FakeGreeter : IGreeter
{
    public string Greet(string name) => $"hello {name}";
}

/// <summary>Handler whose constructor depends on <see cref="IGreeter"/> — fails to resolve if WithService didn't register the fake.</summary>
public sealed class GreetStepHandler : IStepHandler
{
    private readonly IGreeter _greeter;
    public GreetStepHandler(IGreeter greeter) => _greeter = greeter;

    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step) =>
        ValueTask.FromResult<object?>(new StepResult<GreetOutput>
        {
            Key = step.Key,
            Value = new GreetOutput { Message = _greeter.Greet("world") }
        });
}

/// <summary>Output of <see cref="GreetStepHandler"/>.</summary>
public sealed class GreetOutput
{
    public string? Message { get; set; }
}
