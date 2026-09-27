using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Covers <c>flow_step_dispatch_duration_ms</c> on the InMemory runtime (#192): the channel write of an
/// immediate enqueue is timed; a delayed schedule, which returns before doing any work, is not.
/// </summary>
public sealed class InMemoryDispatchDurationMetricTests
{
    [Fact]
    public async Task Enqueue_records_one_ok_measurement_tagged_in_memory()
    {
        // Arrange
        using var telemetry = new FlowOrchestratorTelemetry();
        var tags = new List<(string? Runtime, string? Mode, string? Outcome)>();
        using var listener = Listen(telemetry, tags);
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var sut = new InMemoryStepDispatcher(channel.Writer, telemetry, new FlowObservabilityOptions());

        // Act
        await sut.EnqueueStepAsync(new Core.Execution.ExecutionContext(), Substitute.For<IFlowDefinition>(), new StepInstance("s", "T"));

        // Assert
        var m = Assert.Single(tags);
        Assert.Equal("in_memory", m.Runtime);
        Assert.Equal("enqueue", m.Mode);
        Assert.Equal("ok", m.Outcome);
    }

    [Fact]
    public async Task A_write_to_a_closed_channel_is_recorded_as_error()
    {
        // Arrange — host shutdown completes the channel; the failed dispatch must still be counted.
        using var telemetry = new FlowOrchestratorTelemetry();
        var tags = new List<(string? Runtime, string? Mode, string? Outcome)>();
        using var listener = Listen(telemetry, tags);
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        channel.Writer.Complete();
        var sut = new InMemoryStepDispatcher(channel.Writer, telemetry, new FlowObservabilityOptions());

        // Act
        var ex = await Record.ExceptionAsync(() =>
            sut.EnqueueStepAsync(new Core.Execution.ExecutionContext(), Substitute.For<IFlowDefinition>(), new StepInstance("s", "T")).AsTask());

        // Assert
        Assert.IsType<ChannelClosedException>(ex);
        Assert.Equal("error", Assert.Single(tags).Outcome);
    }

    [Fact]
    public async Task A_delayed_schedule_is_not_timed()
    {
        // Arrange
        using var telemetry = new FlowOrchestratorTelemetry();
        var tags = new List<(string? Runtime, string? Mode, string? Outcome)>();
        using var listener = Listen(telemetry, tags);
        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var sut = new InMemoryStepDispatcher(channel.Writer, telemetry, new FlowObservabilityOptions());

        // Act
        await sut.ScheduleStepAsync(new Core.Execution.ExecutionContext(), Substitute.For<IFlowDefinition>(), new StepInstance("s", "T"), TimeSpan.FromMilliseconds(1));
        await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Empty(tags);
    }

    private static MeterListener Listen(FlowOrchestratorTelemetry telemetry, List<(string? Runtime, string? Mode, string? Outcome)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, telemetry.Meter) && instrument.Name == "flow_step_dispatch_duration_ms")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            string? runtime = null, mode = null, outcome = null;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "runtime": runtime = tag.Value as string; break;
                    case "mode": mode = tag.Value as string; break;
                    case "outcome": outcome = tag.Value as string; break;
                }
            }

            sink.Add((runtime, mode, outcome));
        });
        listener.Start();
        return listener;
    }
}
