using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.ServiceBus;
using global::Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// End-to-end smoke tests verifying that the Service Bus runtime adapter actually carries a
/// step from the dispatcher, through the topic+subscription, into the engine, and out the
/// other side as a handler invocation. Runs against a live SB emulator container.
/// </summary>
[Collection(ServiceBusEmulatorCollection.Name)]
[Trait("Category", "ServiceBusEmulator")]
public class ServiceBusRoundTripTests : IAsyncLifetime
{
    private readonly ServiceBusEmulatorFixture _fixture;
    private IHost? _host;

    public ServiceBusRoundTripTests(ServiceBusEmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        TestStepHandler.Reset();
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHangfire(c => c.UseInMemoryStorage());
                services.AddFlowOrchestrator(opts =>
                {
                    opts.UseInMemory();
                    opts.UseAzureServiceBusRuntime(sb =>
                    {
                        sb.ConnectionString = _fixture.ConnectionString;
                        sb.AutoCreateTopology = false;        // emulator has static config
                    });
                    opts.AddFlow<TestFlow>();
                });
                services.AddStepHandler<TestStepHandler>("TestStep");
            })
            .Build();

        await _host.StartAsync();
        // Give the SB processor a moment to register its message-pump.
        await Task.Delay(500);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
    }

    [Fact]
    public async Task TriggerAsync_DispatchesStepThroughServiceBus_AndHandlerExecutes()
    {
        // Arrange
        Assert.NotNull(_host);
        var orchestrator = _host!.Services.GetRequiredService<IFlowOrchestrator>();
        var flow = _host.Services.GetServices<FlowOrchestrator.Core.Abstractions.IFlowDefinition>()
                                  .First(f => f.Id == ServiceBusEmulatorFixture.TestFlowId);

        var awaiter = TestStepHandler.ResetAndAwait();
        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null),
        };

        // Act
        await orchestrator.TriggerAsync(ctx);
        // Wait for the consumer to drain the message and invoke the handler.
        var completed = await Task.WhenAny(awaiter, Task.Delay(TimeSpan.FromSeconds(60)));

        // Assert
        Assert.Same(awaiter, completed);
        var n = await awaiter;
        Assert.Equal(1, n);
        Assert.Equal(1, TestStepHandler.InvocationCount);
    }

    [Fact]
    public async Task ScheduleStepAsync_DelaysDelivery_ByApproximatelyTheRequestedInterval()
    {
        // Arrange
        Assert.NotNull(_host);
        var dispatcher = _host!.Services.GetRequiredService<IStepDispatcher>();
        var flow = _host.Services.GetServices<FlowOrchestrator.Core.Abstractions.IFlowDefinition>()
                                  .First(f => f.Id == ServiceBusEmulatorFixture.TestFlowId);

        var runId = Guid.NewGuid();
        var runStore = _host.Services.GetRequiredService<FlowOrchestrator.Core.Storage.IFlowRunStore>();
        await runStore.StartRunAsync(flow.Id, nameof(TestFlow), runId, "manual", triggerData: null, jobId: null);
        await runStore.TryRecordDispatchAsync(runId, "only_step");

        var awaiter = TestStepHandler.ResetAndAwait();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("only_step", "TestStep")
        {
            RunId = runId,
            ScheduledTime = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3),
        };
        step.Inputs["payload"] = "delayed";

        // Act
        var sentAt = DateTimeOffset.UtcNow;
        await dispatcher.ScheduleStepAsync(ctx, flow, step, TimeSpan.FromSeconds(3));
        var completed = await Task.WhenAny(awaiter, Task.Delay(TimeSpan.FromSeconds(60)));
        var receivedAt = DateTimeOffset.UtcNow;

        // Assert — handler must have run, and at least most of the requested delay must
        // have elapsed. Upper bound is intentionally absent (per project anti-flake rules).
        Assert.Same(awaiter, completed);
        Assert.Equal(1, TestStepHandler.InvocationCount);
        Assert.True((receivedAt - sentAt).TotalMilliseconds >= 1500,
            $"Step delivered after only {(receivedAt - sentAt).TotalMilliseconds:N0}ms — ScheduledEnqueueTime appears not to have been honoured.");
    }
}
