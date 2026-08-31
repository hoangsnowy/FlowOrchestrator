using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Precedence coverage for run-timeout resolution at trigger time: the per-run
/// <c>runTimeoutSeconds</c> value carried in the trigger payload versus
/// <see cref="FlowRunControlOptions.DefaultRunTimeout"/>, and the shapes of trigger data the
/// resolver actually inspects.
/// </summary>
/// <remarks>
/// Assertions are on the computed absolute deadline relative to the moment of the trigger, with
/// windows wide enough that scheduling jitter cannot matter — no test waits for a deadline to lapse.
/// </remarks>
public sealed class RunTimeoutResolutionTests
{
    private static IFlowDefinition MakeSingleStepFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection { ["step1"] = new StepMetadata { Type = "Work" } }
        });
        return flow;
    }

    private static LoopBarrierEngineHarness Harness(TimeSpan? defaultRunTimeout) =>
        new(MakeSingleStepFlow(),
            key => new StepResult { Key = key, Status = StepStatus.Succeeded },
            new FlowRunControlOptions { DefaultRunTimeout = defaultRunTimeout });

    private static async Task<TimeSpan?> DeadlineOffsetAsync(LoopBarrierEngineHarness harness, Guid runId, DateTimeOffset triggeredAt)
    {
        var control = await harness.Store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        return control!.TimeoutAtUtc is { } at ? at - triggeredAt : null;
    }

    [Fact]
    public async Task TriggerAsync_withRunTimeoutSecondsInTheBody_overridesDefaultRunTimeout()
    {
        // Arrange - a short global default and a long per-run override.
        var harness = Harness(TimeSpan.FromSeconds(30));
        var triggeredAt = DateTimeOffset.UtcNow;

        // Act
        var runId = await harness.TriggerAsync(new Dictionary<string, object?> { ["runTimeoutSeconds"] = 3600 });

        // Assert - the per-run value wins.
        var offset = await DeadlineOffsetAsync(harness, runId, triggeredAt);
        Assert.NotNull(offset);
        Assert.True(offset > TimeSpan.FromMinutes(30), $"deadline offset was {offset}, expected the 3600s override");
    }

    [Fact]
    public async Task TriggerAsync_withoutOverride_usesDefaultRunTimeout()
    {
        // Arrange
        var harness = Harness(TimeSpan.FromHours(2));
        var triggeredAt = DateTimeOffset.UtcNow;

        // Act
        var runId = await harness.TriggerAsync();

        // Assert
        var offset = await DeadlineOffsetAsync(harness, runId, triggeredAt);
        Assert.NotNull(offset);
        Assert.True(offset > TimeSpan.FromMinutes(90), $"deadline offset was {offset}, expected the 2h default");
    }

    [Fact]
    public async Task TriggerAsync_withNonPositiveOverride_fallsBackToDefaultRunTimeout()
    {
        // Arrange - a zero override is not "no timeout"; it is an invalid value that must be ignored.
        var harness = Harness(TimeSpan.FromHours(2));
        var triggeredAt = DateTimeOffset.UtcNow;

        // Act
        var runId = await harness.TriggerAsync(new Dictionary<string, object?> { ["runTimeoutSeconds"] = 0 });

        // Assert
        var offset = await DeadlineOffsetAsync(harness, runId, triggeredAt);
        Assert.NotNull(offset);
        Assert.True(offset > TimeSpan.FromMinutes(90), $"deadline offset was {offset}, expected the 2h default");
    }

    [Fact]
    public async Task TriggerAsync_withNoDefaultAndNoOverride_leavesTheRunUnbounded()
    {
        // Arrange
        var harness = Harness(defaultRunTimeout: null);

        // Act
        var runId = await harness.TriggerAsync();

        // Assert
        var control = await harness.Store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimeoutAtUtc);
    }

    [Fact]
    public async Task TriggerAsync_withNoDefaultButAnOverride_boundsTheRun()
    {
        // Arrange - a per-run override must work even when no global default is configured.
        var harness = Harness(defaultRunTimeout: null);
        var triggeredAt = DateTimeOffset.UtcNow;

        // Act
        var runId = await harness.TriggerAsync(new Dictionary<string, object?> { ["runTimeoutSeconds"] = 600 });

        // Assert
        var offset = await DeadlineOffsetAsync(harness, runId, triggeredAt);
        Assert.NotNull(offset);
        Assert.True(offset > TimeSpan.FromMinutes(5), $"deadline offset was {offset}, expected the 600s override");
    }

    [Fact]
    public async Task TriggerAsync_withAJsonElementBody_honoursTheOverride()
    {
        // Arrange - the production shape: webhook and dashboard triggers arrive as a JsonElement.
        var harness = Harness(TimeSpan.FromSeconds(30));
        var body = JsonSerializer.SerializeToElement(new { runTimeoutSeconds = 3600 });
        var triggeredAt = DateTimeOffset.UtcNow;

        // Act
        var runId = await harness.TriggerAsync(body);

        // Assert
        var offset = await DeadlineOffsetAsync(harness, runId, triggeredAt);
        Assert.NotNull(offset);
        Assert.True(offset > TimeSpan.FromMinutes(30), $"deadline offset was {offset}, expected the 3600s override");
    }

    [Fact]
    public async Task TriggerAsync_withTheOverrideOnAPlainObjectBody_silentlyUsesTheDefault()
    {
        // Arrange - pins a sharp edge: ResolveTimeoutFromTriggerData only inspects JsonElement and
        // IDictionary payloads, so an in-process trigger passing an anonymous/POCO body gets the
        // global default with no warning.
        var harness = Harness(TimeSpan.FromSeconds(30));
        var triggeredAt = DateTimeOffset.UtcNow;

        // Act
        var runId = await harness.TriggerAsync(new { runTimeoutSeconds = 3600 });

        // Assert
        var offset = await DeadlineOffsetAsync(harness, runId, triggeredAt);
        Assert.NotNull(offset);
        Assert.True(offset < TimeSpan.FromMinutes(5), $"deadline offset was {offset}, expected the 30s default");
    }
}
