using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// End-to-end coverage for issue #166: a nested ForEach child step referencing a sibling
/// child's output by bare key (<c>@steps('emit')</c>) must resolve to the current iteration's
/// runtime key through the real engine + InMemory runtime + outputs repository.
/// </summary>
public sealed class ForEachSiblingReferenceTests
{
    [Fact]
    public async Task ForEachChild_resolvesSiblingOutputByBareKey_perIteration()
    {
        // Arrange
        await using var host = await FlowTestHost.For<ForEachSiblingReferenceFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<EmitIndexStepHandler>("EmitIndex")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(
            body: new { items = new[] { "a", "b", "c" } },
            timeout: TimeSpan.FromSeconds(15));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["finalize"].Status);
        for (var index = 0; index < 3; index++)
        {
            // Each consume step read its own iteration's emit output, not iteration 0's.
            var consume = result.Steps[$"process_items.{index}.consume"];
            Assert.Equal(StepStatus.Succeeded, consume.Status);
            var echoed = consume.Output.GetProperty("Echoed").GetString();
            Assert.Equal($"iter-{index}", echoed);
        }
    }
}
