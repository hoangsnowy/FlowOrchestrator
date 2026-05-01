using System.Diagnostics.Metrics;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Observability;

/// <summary>
/// Unit tests for the two histograms wired in the v1.19 observability work:
/// <c>flow_signal_wait_ms</c> (parked-step wait time, recorded by <see cref="FlowSignalDispatcher"/>)
/// and <c>flow_cron_lag_ms</c> (cron scheduled-vs-fired delta, recorded by the InMemory and
/// Hangfire recurring dispatchers).
/// </summary>
public sealed class SignalAndCronMetricsTests
{
    [Fact]
    public async Task FlowSignalDispatcher_RecordsSignalWaitMs_OnDelivery()
    {
        // Arrange
        var telemetry = new FlowOrchestratorTelemetry();
        var captured = new List<double>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == FlowOrchestratorTelemetry.SourceName && instr.Name == "flow_signal_wait_ms")
                {
                    l.EnableMeasurementEvents(instr);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, _, _) => captured.Add(value));
        meterListener.Start();

        var runId = Guid.NewGuid();
        var stepKey = "wait_for_approval";
        var signalName = "approve";
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var deliveredAt = DateTimeOffset.UtcNow;

        var signalStore = Substitute.For<IFlowSignalStore>();
        signalStore.DeliverSignalAsync(runId, signalName, "{}", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(new SignalDeliveryResult(SignalDeliveryStatus.Delivered, stepKey, deliveredAt)));
        signalStore.GetWaiterAsync(runId, stepKey, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FlowSignalWaiter?>(new FlowSignalWaiter
            {
                RunId = runId,
                StepKey = stepKey,
                SignalName = signalName,
                CreatedAt = createdAt,
                DeliveredAt = deliveredAt,
            }));

        var runStore = Substitute.For<IFlowRunStore>();
        runStore.GetRunDetailAsync(runId).Returns(Task.FromResult<FlowRunRecord?>(new FlowRunRecord
        {
            Id = runId,
            FlowId = Guid.NewGuid(),
            FlowName = "Approval",
            Status = "Running",
            TriggerKey = "manual",
            StartedAt = DateTimeOffset.UtcNow,
        }));

        var flowRepo = Substitute.For<IFlowRepository>();
        flowRepo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(Array.Empty<IFlowDefinition>()));

        var dispatcher = new FlowSignalDispatcher(
            signalStore,
            runStore,
            flowRepo,
            Substitute.For<IStepDispatcher>(),
            Substitute.For<IOutputsRepository>(),
            telemetry);

        // Act
        await dispatcher.DispatchAsync(runId, signalName, "{}");

        // Assert
        var recorded = Assert.Single(captured);
        // ~5 seconds = ~5000 ms; allow generous slack for the wall-clock between Arrange and Act.
        Assert.InRange(recorded, 4_000, 10_000);
    }

    [Fact]
    public async Task FlowSignalDispatcher_DoesNotRecord_WhenTelemetryNotInjected()
    {
        // Arrange — same setup as above but telemetry == null
        var captured = new List<double>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == FlowOrchestratorTelemetry.SourceName && instr.Name == "flow_signal_wait_ms")
                {
                    l.EnableMeasurementEvents(instr);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, _, _) => captured.Add(value));
        meterListener.Start();

        var runId = Guid.NewGuid();
        var signalStore = Substitute.For<IFlowSignalStore>();
        signalStore.DeliverSignalAsync(runId, "approve", "{}", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(new SignalDeliveryResult(SignalDeliveryStatus.Delivered, "wait", DateTimeOffset.UtcNow)));

        var runStore = Substitute.For<IFlowRunStore>();
        runStore.GetRunDetailAsync(runId).Returns(Task.FromResult<FlowRunRecord?>(new FlowRunRecord
        {
            Id = runId,
            FlowId = Guid.NewGuid(),
            FlowName = "X",
            Status = "Running",
            TriggerKey = "manual",
            StartedAt = DateTimeOffset.UtcNow,
        }));

        var flowRepo = Substitute.For<IFlowRepository>();
        flowRepo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(Array.Empty<IFlowDefinition>()));

        var dispatcher = new FlowSignalDispatcher(
            signalStore,
            runStore,
            flowRepo,
            Substitute.For<IStepDispatcher>(),
            Substitute.For<IOutputsRepository>(),
            telemetry: null);

        // Act
        await dispatcher.DispatchAsync(runId, "approve", "{}");

        // Assert
        Assert.Empty(captured);
        // GetWaiterAsync should not be called when telemetry is null — saves a DB roundtrip.
        await signalStore.DidNotReceive().GetWaiterAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
