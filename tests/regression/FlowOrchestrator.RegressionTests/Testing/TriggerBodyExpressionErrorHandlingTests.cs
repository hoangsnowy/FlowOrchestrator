using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression for trigger-expression resolution under degenerate inputs. When a step input
/// references <c>@triggerBody()?.path</c> but the trigger body is <see langword="null"/>
/// or the path is missing, the engine must:
/// <list type="number">
///   <item>NOT hang — the run reaches a terminal state within the test budget.</item>
///   <item>NOT throw a <see cref="System.NullReferenceException"/> from the resolver.</item>
///   <item>Resolve the expression to <see langword="null"/> and pass that into the handler.</item>
/// </list>
/// </summary>
public sealed class TriggerBodyExpressionErrorHandlingTests
{
    [Fact]
    public async Task TriggerWithNullBody_StepInputResolvesToNull_AndRunReachesTerminal()
    {
        // Arrange
        var probe = new ExpressionEchoProbe();
        await using var host = await FlowTestHost.For<MalformedExpressionFlow>()
            .WithService(probe)
            .WithHandler<EchoInputStepHandler>("EchoInput")
            .BuildAsync();

        // Act — trigger with null body so the optional-chain expression resolves to null.
        var result = await host.TriggerAsync(body: null, timeout: TimeSpan.FromSeconds(30));

        // Assert — run terminated, handler ran exactly once, observed a null input.
        Assert.False(result.TimedOut);
        Assert.Equal(1, probe.Calls);
        Assert.Null(probe.LastResolvedValue);
    }

    [Fact]
    public async Task TriggerWithBodyMissingPath_StepInputResolvesToNull_AndRunReachesTerminal()
    {
        // Arrange — body is non-null but the requested path is absent.
        var probe = new ExpressionEchoProbe();
        await using var host = await FlowTestHost.For<MalformedExpressionFlow>()
            .WithService(probe)
            .WithHandler<EchoInputStepHandler>("EchoInput")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(body: new { unrelated = "value" }, timeout: TimeSpan.FromSeconds(30));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(1, probe.Calls);
        Assert.Null(probe.LastResolvedValue);
    }
}
