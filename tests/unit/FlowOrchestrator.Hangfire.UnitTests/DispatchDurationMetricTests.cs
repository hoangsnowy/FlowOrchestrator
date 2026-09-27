using System.Diagnostics.Metrics;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using NSubstitute;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// Covers <c>flow_step_dispatch_duration_ms</c> on the Hangfire runtime: one measurement per
/// submission, tagged with runtime, mode and outcome, and nothing when OpenTelemetry is disabled.
/// </summary>
/// <remarks>
/// The histogram exists to answer #192's open question — whether a runtime's trigger-latency tail
/// sits on the dispatch itself — so its tags are the contract under test.
/// </remarks>
public sealed class DispatchDurationMetricTests
{
    private readonly IBackgroundJobClient _client = Substitute.For<IBackgroundJobClient>();

    [Fact]
    public async Task Enqueue_records_one_ok_measurement_tagged_hangfire_enqueue()
    {
        // Arrange
        _client.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-1");
        using var telemetry = new FlowOrchestratorTelemetry();
        using var capture = new DispatchCapture(telemetry);
        var sut = new HangfireStepDispatcher(_client, telemetry, new FlowObservabilityOptions());

        // Act
        await sut.EnqueueStepAsync(Ctx(), Flow(), Step(), CancellationToken.None);

        // Assert
        var m = Assert.Single(capture.Measurements);
        Assert.Equal("hangfire", m.Runtime);
        Assert.Equal("enqueue", m.Mode);
        Assert.Equal("ok", m.Outcome);
        Assert.True(m.Value >= 0);
    }

    [Fact]
    public async Task Schedule_records_mode_schedule()
    {
        // Arrange
        _client.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-2");
        using var telemetry = new FlowOrchestratorTelemetry();
        using var capture = new DispatchCapture(telemetry);
        var sut = new HangfireStepDispatcher(_client, telemetry, new FlowObservabilityOptions());

        // Act
        await sut.ScheduleStepAsync(Ctx(), Flow(), Step(), TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert
        Assert.Equal("schedule", Assert.Single(capture.Measurements).Mode);
    }

    [Fact]
    public async Task A_failing_submission_is_recorded_as_error_and_still_throws()
    {
        // Arrange — a job-storage outage must show up in the histogram, not vanish from it.
        _client.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(_ => throw new InvalidOperationException("storage down"));
        using var telemetry = new FlowOrchestratorTelemetry();
        using var capture = new DispatchCapture(telemetry);
        var sut = new HangfireStepDispatcher(_client, telemetry, new FlowObservabilityOptions());

        // Act
        var ex = await Record.ExceptionAsync(() => sut.EnqueueStepAsync(Ctx(), Flow(), Step(), CancellationToken.None).AsTask());

        // Assert
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal("error", Assert.Single(capture.Measurements).Outcome);
    }

    [Fact]
    public async Task Nothing_is_recorded_when_OpenTelemetry_is_disabled()
    {
        // Arrange
        _client.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-3");
        using var telemetry = new FlowOrchestratorTelemetry();
        using var capture = new DispatchCapture(telemetry);
        var sut = new HangfireStepDispatcher(_client, telemetry, new FlowObservabilityOptions { EnableOpenTelemetry = false });

        // Act
        await sut.EnqueueStepAsync(Ctx(), Flow(), Step(), CancellationToken.None);

        // Assert
        Assert.Empty(capture.Measurements);
    }

    private static Core.Execution.ExecutionContext Ctx() => new() { RunId = Guid.NewGuid() };

    private static StepInstance Step() => new("step1", "DoWork");

    private static IFlowDefinition Flow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        return flow;
    }

    /// <summary>Captures <c>flow_step_dispatch_duration_ms</c> measurements from one telemetry instance.</summary>
    internal sealed class DispatchCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public DispatchCapture(FlowOrchestratorTelemetry telemetry)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, telemetry.Meter) && instrument.Name == "flow_step_dispatch_duration_ms")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
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

                Measurements.Add((value, runtime, mode, outcome));
            });
            _listener.Start();
        }

        public List<(double Value, string? Runtime, string? Mode, string? Outcome)> Measurements { get; } = [];

        public void Dispose() => _listener.Dispose();
    }
}
