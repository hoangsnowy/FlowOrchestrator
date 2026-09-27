using System.Diagnostics.Metrics;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.ServiceBus;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Covers <c>flow_step_dispatch_duration_ms</c> on the Service Bus runtime — the measurement #192 needs
/// to tell whether its 470x p50/p95 trigger-latency spread sits on the send.
/// </summary>
public sealed class ServiceBusDispatchDurationMetricTests
{
    private readonly ServiceBusClient _client = Substitute.For<ServiceBusClient>();
    private readonly ServiceBusSender _sender = Substitute.For<ServiceBusSender>();

    /// <summary>Routes the dispatcher's lazily created sender to the substitute.</summary>
    public ServiceBusDispatchDurationMetricTests()
    {
        _client.CreateSender(Arg.Any<string>()).Returns(_sender);
    }

    [Theory]
    [InlineData(false, "enqueue")]
    [InlineData(true, "schedule")]
    public async Task Each_send_records_one_ok_measurement_tagged_service_bus(bool scheduled, string expectedMode)
    {
        // Arrange
        using var telemetry = new FlowOrchestratorTelemetry();
        var tags = new List<(string? Runtime, string? Mode, string? Outcome)>();
        using var listener = Listen(telemetry, tags);
        var sut = new ServiceBusStepDispatcher(_client, new ServiceBusRuntimeOptions(), telemetry, new FlowObservabilityOptions());

        // Act
        if (scheduled)
        {
            await sut.ScheduleStepAsync(Ctx(), Flow(), Step(), TimeSpan.FromSeconds(5));
        }
        else
        {
            await sut.EnqueueStepAsync(Ctx(), Flow(), Step());
        }

        // Assert
        var m = Assert.Single(tags);
        Assert.Equal("service_bus", m.Runtime);
        Assert.Equal(expectedMode, m.Mode);
        Assert.Equal("ok", m.Outcome);
    }

    [Fact]
    public async Task A_failed_send_is_recorded_as_error_and_still_throws()
    {
        // Arrange
        _sender.SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy));
        using var telemetry = new FlowOrchestratorTelemetry();
        var tags = new List<(string? Runtime, string? Mode, string? Outcome)>();
        using var listener = Listen(telemetry, tags);
        var sut = new ServiceBusStepDispatcher(_client, new ServiceBusRuntimeOptions(), telemetry, new FlowObservabilityOptions());

        // Act
        var ex = await Record.ExceptionAsync(() => sut.EnqueueStepAsync(Ctx(), Flow(), Step()).AsTask());

        // Assert
        Assert.IsType<ServiceBusException>(ex);
        Assert.Equal("error", Assert.Single(tags).Outcome);
    }

    [Fact]
    public async Task Nothing_is_recorded_without_telemetry()
    {
        // Arrange — positional two-argument construction, as before this metric existed.
        using var telemetry = new FlowOrchestratorTelemetry();
        var tags = new List<(string? Runtime, string? Mode, string? Outcome)>();
        using var listener = Listen(telemetry, tags);
        var sut = new ServiceBusStepDispatcher(_client, new ServiceBusRuntimeOptions());

        // Act
        await sut.EnqueueStepAsync(Ctx(), Flow(), Step());

        // Assert
        Assert.Empty(tags);
        await _sender.Received(1).SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>());
    }

    private static Core.Execution.ExecutionContext Ctx() => new() { RunId = Guid.NewGuid() };

    private static StepInstance Step() => new("s", "T");

    private static IFlowDefinition Flow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        return flow;
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
