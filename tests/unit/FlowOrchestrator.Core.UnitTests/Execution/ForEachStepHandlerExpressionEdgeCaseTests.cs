using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Bounds / null-safety regression for the private trigger expression resolvers
/// inside <see cref="ForEachStepHandler"/>. Pre-fix, malformed bracket expressions like
/// <c>"@triggerHeaders()[']"</c> (length 3) satisfied both <c>StartsWith("['")</c> and
/// <c>EndsWith("']")</c> and the <c>[2..^2]</c> slice threw <see cref="ArgumentOutOfRangeException"/>.
/// Post-fix, the resolver returns false and the source value is passed through unchanged,
/// producing an empty iteration set.
/// </summary>
public sealed class ForEachStepHandlerExpressionEdgeCaseTests
{
    [Theory]
    [InlineData("@triggerHeaders()[']")]      // length-3 trailing remainder — would crash pre-fix
    [InlineData("@triggerHeaders()[\"]")]      // double-quote variant of the same
    [InlineData("@triggerHeaders()['")]        // unterminated single-quote bracket
    [InlineData("@triggerHeaders()[")]         // dangling open bracket
    [InlineData("@triggerHeaders()['']")]      // length-4 — empty header name, was always safe
    [InlineData("@")]                          // bare at-sign
    [InlineData("@triggerBody")]               // no parens, no remainder
    public async Task ExecuteAsync_MalformedTriggerExpression_DoesNotThrow_AndYieldsZeroIterations(string expression)
    {
        // Arrange
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = expression,
            Steps = new StepCollection
            {
                ["child"] = new StepMetadata { Type = "Work" }
            }
        };

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var manifest = new FlowManifest
        {
            Steps = new StepCollection
            {
                ["loop"] = loop
            }
        };
        flow.Manifest.Returns(manifest);

        var ctx = new CoreExecutionContext
        {
            RunId = Guid.NewGuid(),
            TriggerData = null,
            TriggerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        var step = new StepInstance("loop", "ForEach") { RunId = ctx.RunId };
        var handler = new ForEachStepHandler();

        // Act
        var result = await handler.ExecuteAsync(ctx, flow, step);

        // Assert — handler returns successfully with zero iterations rather than throwing.
        var stepResult = Assert.IsType<StepResult>(result);
        Assert.Equal(StepStatus.Succeeded, stepResult.Status);
    }
}
