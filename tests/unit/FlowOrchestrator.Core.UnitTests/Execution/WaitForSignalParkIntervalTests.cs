using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Covers the safety-net park interval <see cref="WaitForSignalHandler"/> returns for a step that
/// declares no <c>timeoutSeconds</c> (issue #190).
/// </summary>
/// <remarks>
/// The interval is the worst-case resume latency if every other resume path fails, because each
/// safety-net invocation re-reads the waiter and completes the step if a payload landed. It was a
/// hard-coded 24 hours; the default is unchanged, but it is now configurable through
/// <see cref="FlowSignalOptions.IndefiniteParkInterval"/>.
/// </remarks>
public sealed class WaitForSignalParkIntervalTests
{
    private sealed class SignalStepInstance(string key, WaitForSignalInput inputs) : IStepInstance<WaitForSignalInput>
    {
        public Guid RunId { get; set; }
        public string? PrincipalId { get; set; }
        public object? TriggerData { get; set; }
        public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
        public string? JobId { get; set; }
        public DateTimeOffset ScheduledTime { get; set; }
        public string Type { get; set; } = "WaitForSignal";
        public string Key { get; } = key;
        public WaitForSignalInput Inputs { get; set; } = inputs;
        public int Index { get; set; }
        public bool ScopeMoveNext { get; set; }
    }

    [Fact]
    public async Task Parks_for_the_24_hour_default_when_no_timeout_is_declared()
    {
        // Arrange
        var handler = new WaitForSignalHandler(new InMemoryFlowSignalStore());

        // Act
        var result = await ParkAsync(handler);

        // Assert
        Assert.Equal(StepStatus.Pending, result.Status);
        Assert.Equal(TimeSpan.FromHours(24), result.DelayNextStep);
    }

    [Fact]
    public async Task Parks_for_the_configured_interval_on_every_invocation()
    {
        // Arrange
        var options = new FlowSignalOptions { IndefiniteParkInterval = TimeSpan.FromSeconds(45) };
        var handler = new WaitForSignalHandler(new InMemoryFlowSignalStore(), options: options);
        var runId = Guid.NewGuid();

        // Act — first invocation registers the waiter, the second is a safety-net re-invocation.
        var first = await ParkAsync(handler, runId);
        var second = await ParkAsync(handler, runId);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(45), first.DelayNextStep);
        Assert.Equal(TimeSpan.FromSeconds(45), second.DelayNextStep);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task Ignores_a_non_positive_interval_and_uses_the_default(int seconds)
    {
        // Arrange — a zero delay would re-invoke the step in a hot loop.
        var options = new FlowSignalOptions { IndefiniteParkInterval = TimeSpan.FromSeconds(seconds) };
        var handler = new WaitForSignalHandler(new InMemoryFlowSignalStore(), options: options);

        // Act
        var result = await ParkAsync(handler);

        // Assert
        Assert.Equal(FlowSignalOptions.DefaultIndefiniteParkInterval, result.DelayNextStep);
    }

    public static TheoryData<TimeSpan> TooLarge() =>
    [
        TimeSpan.FromDays(60),   // past Task.Delay's ~49.7-day ceiling: the InMemory safety net never fired
        TimeSpan.MaxValue,       // UtcNow + delay overflows in every dispatcher: a poison job
    ];

    [Theory]
    [MemberData(nameof(TooLarge))]
    public async Task Clamps_an_interval_no_runtime_can_honour_to_thirty_days(TimeSpan configured)
    {
        // Arrange
        var options = new FlowSignalOptions { IndefiniteParkInterval = configured };
        var handler = new WaitForSignalHandler(new InMemoryFlowSignalStore(), options: options);

        // Act
        var result = await ParkAsync(handler);

        // Assert
        Assert.Equal(TimeSpan.FromDays(30), result.DelayNextStep);
        Assert.Equal(FlowSignalOptions.MaxIndefiniteParkInterval, result.DelayNextStep);
        _ = DateTimeOffset.UtcNow + result.DelayNextStep!.Value; // must not overflow
    }

    [Fact]
    public async Task A_declared_timeout_still_wins_over_the_park_interval()
    {
        // Arrange
        var options = new FlowSignalOptions { IndefiniteParkInterval = TimeSpan.FromSeconds(45) };
        var handler = new WaitForSignalHandler(new InMemoryFlowSignalStore(), options: options);

        // Act
        var result = await ParkAsync(handler, timeoutSeconds: 600);

        // Assert — timeout + 1 s grace, not the indefinite interval.
        Assert.Equal(TimeSpan.FromSeconds(601), result.DelayNextStep);
    }

    private static async Task<StepResult> ParkAsync(WaitForSignalHandler handler, Guid? runId = null, int? timeoutSeconds = null)
    {
        var id = runId ?? Guid.NewGuid();
        var step = new SignalStepInstance("wait", new WaitForSignalInput { SignalName = "go", TimeoutSeconds = timeoutSeconds })
        {
            RunId = id
        };

        return (StepResult)(await handler.ExecuteAsync(
            new Core.Execution.ExecutionContext { RunId = id }, Substitute.For<IFlowDefinition>(), step))!;
    }
}
