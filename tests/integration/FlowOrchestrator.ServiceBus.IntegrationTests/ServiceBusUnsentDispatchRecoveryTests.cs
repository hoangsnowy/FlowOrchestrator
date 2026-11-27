using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// End-to-end proof, against a live Service Bus emulator, that a step whose dispatch-ledger row was written
/// but whose send never happened — what a replica killed mid-send leaves behind (stress harness,
/// 2026-11-24: 26 runs stranded for ever) — is re-sent by the unsent-dispatch sweep, delivered through the
/// topic and subscription, and executed.
/// </summary>
[Collection(ServiceBusEmulatorCollection.Name)]
[Trait("Category", "ServiceBusEmulator")]
public sealed class ServiceBusUnsentDispatchRecoveryTests : IAsyncLifetime
{
    private readonly ServiceBusEmulatorFixture _fixture;
    private IHost? _host;

    /// <summary>Receives the shared emulator.</summary>
    /// <param name="fixture">The emulator container shared by the collection.</param>
    public ServiceBusUnsentDispatchRecoveryTests(ServiceBusEmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        TestStepHandler.Reset();
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFlowOrchestrator(opts =>
                {
                    opts.UseInMemory();
                    opts.UseAzureServiceBusRuntime(sb =>
                    {
                        sb.ConnectionString = _fixture.ConnectionString;
                        sb.AutoCreateTopology = false;
                    });
                    opts.AddFlow<TestFlow>();
                    // Short, so the test waits seconds rather than a minute; the sweep runs every 1 s.
                    opts.RunControl.UnsentDispatchTimeout = TimeSpan.FromSeconds(2);
                });
                services.AddStepHandler<TestStepHandler>("TestStep");
            })
            .Build();

        await _host.StartAsync();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
    }

    [Fact]
    public async Task A_ledgered_step_whose_send_never_happened_is_resent_by_the_sweep_and_executes()
    {
        // Arrange — the state a replica killed between the ledger write and the send's return leaves: the
        // run is Running and "only_step" holds a dispatch-ledger row with no message id; nothing is queued.
        var runStore = _host!.Services.GetRequiredService<IFlowRunStore>();
        var runId = Guid.NewGuid();
        var awaiter = TestStepHandler.ResetAndAwait();
        await runStore.StartRunAsync(ServiceBusEmulatorFixture.TestFlowId, nameof(TestFlow), runId, "manual", triggerData: null, jobId: null);

        // Act
        var recorded = await runStore.TryRecordDispatchAsync(runId, "only_step");
        var invocation = await awaiter.WaitAsync(TimeSpan.FromSeconds(60));

        // Assert — the sweep leased the row once it was older than the timeout, re-sent it through the
        // emulator, and the processor ran the step.
        Assert.True(recorded);
        Assert.Equal(1, invocation);
        Assert.True(await runStore.IsStepDispatchedAsync(runId, "only_step"));
    }
}
