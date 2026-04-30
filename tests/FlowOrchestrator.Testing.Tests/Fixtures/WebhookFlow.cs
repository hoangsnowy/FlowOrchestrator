using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Single-step flow whose only step's input is resolved from a trigger header.
/// Used by <see cref="WebhookTests"/> to verify <c>@triggerHeaders()</c> expression resolution.
/// </summary>
public sealed class WebhookFlow : IFlowDefinition
{
    public Guid Id { get; } = new("66666666-6666-6666-6666-666666666666");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["demo-hook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?> { ["webhookSlug"] = "demo-hook" }
            }
        },
        Steps = new StepCollection
        {
            ["capture"] = new StepMetadata
            {
                Type = "CaptureHeader",
                Inputs = new Dictionary<string, object?>
                {
                    ["foo"] = "@triggerHeaders()['X-Foo']"
                }
            }
        }
    };
}

/// <summary>Inputs for <see cref="CaptureHeaderStepHandler"/>.</summary>
public sealed class CaptureHeaderInput
{
    public string? Foo { get; set; }
}

/// <summary>Handler that returns the resolved <c>foo</c> input as output.</summary>
public sealed class CaptureHeaderStepHandler : IStepHandler<CaptureHeaderInput>
{
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance<CaptureHeaderInput> step) =>
        ValueTask.FromResult<object?>(new StepResult<CaptureHeaderOutput>
        {
            Key = step.Key,
            Value = new CaptureHeaderOutput { Captured = step.Inputs.Foo }
        });
}

/// <summary>Output of <see cref="CaptureHeaderStepHandler"/>.</summary>
public sealed class CaptureHeaderOutput
{
    public string? Captured { get; set; }
}
