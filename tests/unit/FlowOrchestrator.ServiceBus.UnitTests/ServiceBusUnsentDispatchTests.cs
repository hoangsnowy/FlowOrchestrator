using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// The 2026-11-24 stress-harness strand (SQL Server + Service Bus, 3 replicas, 6 kills) reproduced against the
/// real <see cref="ServiceBusStepDispatcher"/> over a substituted <see cref="ServiceBusSender"/>: a
/// <see cref="ServiceBusSender.SendMessageAsync(ServiceBusMessage, CancellationToken)"/> that never returns —
/// the process was killed mid-send, or the send is stuck inside the client's retry loop (TryTimeout 60 s,
/// three retries) — leaves the successor's dispatch-ledger row without a message id, which used to strand
/// the run for ever.
/// </summary>
public sealed class ServiceBusUnsentDispatchTests : IAsyncDisposable
{
    private readonly ServiceBusClient _client = Substitute.For<ServiceBusClient>();
    private readonly ServiceBusSender _sender = Substitute.For<ServiceBusSender>();
    private readonly InMemoryFlowRunStore _store = new();
    private readonly InMemoryOutputsRepository _outputs = new();
    private readonly IStepExecutor _executor = Substitute.For<IStepExecutor>();
    private readonly List<(ServiceBusMessage Message, CancellationToken Token)> _sends = [];
    private readonly TaskCompletionSource _hungSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IFlowDefinition _flow;
    private readonly Guid _runId = Guid.NewGuid();
    private readonly ServiceBusStepDispatcher _dispatcher;
    private FlowUnsentDispatchRecoveryHostedService? _sweep;

    /// <summary>Wires a flow <c>a → b</c> whose first Service Bus send never completes.</summary>
    public ServiceBusUnsentDispatchTests()
    {
        _client.CreateSender(Arg.Any<string>()).Returns(_sender);
        _sender.SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                int count;
                lock (_sends)
                {
                    _sends.Add((call.ArgAt<ServiceBusMessage>(0), call.ArgAt<CancellationToken>(1)));
                    count = _sends.Count;
                }

                if (count == 1)
                {
                    _firstSend.TrySetResult();
                    return _hungSend.Task;
                }

                _secondSend.TrySetResult();
                return Task.CompletedTask;
            });
        _dispatcher = new ServiceBusStepDispatcher(_client, new ServiceBusRuntimeOptions());

        _flow = Substitute.For<IFlowDefinition>();
        _flow.Id.Returns(Guid.NewGuid());
        _flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "Work" },
                ["b"] = new StepMetadata { Type = "Work", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } }
            }
        });
    }

    [Fact]
    public async Task A_send_that_never_returns_is_resent_as_a_new_message_by_the_unsent_dispatch_sweep()
    {
        // Arrange — the processor's token is cancelled while "a" runs (processor stopping / lock lost); it
        // must reach neither the stranded send nor the re-send.
        using var processorToken = new CancellationTokenSource();
        _executor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Returns(call =>
            {
                processorToken.Cancel();
                return new ValueTask<IStepResult>(new StepResult { Key = call.ArgAt<IStepInstance>(2).Key, Status = StepStatus.Succeeded });
            });
        await _store.StartRunAsync(_flow.Id, "SbStrandFlow", _runId, "manual", null, null);
        var stuck = CreateEngine().RunStepAsync(
            new CoreExecutionContext { RunId = _runId }, _flow, new StepInstance("a", "Work") { RunId = _runId }, processorToken.Token).AsTask();
        await _firstSend.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var strandedRowLedgered = (await _store.GetDispatchedStepKeysAsync(_runId)).Contains("b");
        _sweep = CreateSweep(TimeSpan.FromMilliseconds(1));

        // Act
        await _sweep.StartAsync(CancellationToken.None);
        await _secondSend.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — the re-send is a fresh message for the same step (a fresh id, so topic duplicate detection
        // cannot drop it), neither send carried a cancellable token, and the re-send's id annotates the row.
        Assert.True(strandedRowLedgered);
        var sends = Snapshot();
        Assert.Equal(2, sends.Count);
        Assert.All(sends, s => Assert.Equal("b", s.Message.Subject));
        Assert.All(sends, s => Assert.Equal(_runId.ToString(), s.Message.ApplicationProperties["RunId"]));
        Assert.All(sends, s => Assert.Equal(_flow.Id.ToString(), s.Message.ApplicationProperties["FlowId"]));
        Assert.NotEqual(sends[0].Message.MessageId, sends[1].Message.MessageId);
        Assert.All(sends, s => Assert.False(s.Token.CanBeCanceled));
        await _sweep.StopAsync(CancellationToken.None);
        Assert.Empty(await _store.LeaseUnsentDispatchesAsync(TimeSpan.Zero, 10));
        _hungSend.TrySetResult();
        await stuck.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Stops the sweep and releases the hung send so the engine call can finish.</summary>
    public async ValueTask DisposeAsync()
    {
        _hungSend.TrySetResult();
        if (_sweep is not null)
        {
            await _sweep.StopAsync(CancellationToken.None);
            _sweep.Dispose();
        }

        await _dispatcher.DisposeAsync();
    }

    private List<(ServiceBusMessage Message, CancellationToken Token)> Snapshot()
    {
        lock (_sends)
        {
            return [.. _sends];
        }
    }

    private FlowOrchestratorEngine CreateEngine() =>
        new(
            _dispatcher,
            new FlowGraphPlanner(),
            _executor,
            Substitute.For<IFlowStore>(),
            _store,
            _outputs,
            Substitute.For<IExecutionContextAccessor>(),
            new FlowRegistry([_flow]),
            [_store],
            [_store],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            Substitute.For<ILogger<FlowOrchestratorEngine>>(),
            NoopFlowEventNotifier.Instance);

    private FlowUnsentDispatchRecoveryHostedService CreateSweep(TimeSpan timeout)
    {
        var services = new ServiceCollection()
            .AddSingleton<IFlowRunStore>(_store)
            .AddSingleton(new FlowRegistry([_flow]))
            .AddSingleton<IStepDispatcher>(_dispatcher)
            .AddSingleton<IOutputsRepository>(_outputs)
            .BuildServiceProvider();
        return new FlowUnsentDispatchRecoveryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FlowRunControlOptions { UnsentDispatchTimeout = timeout },
            NullLogger<FlowUnsentDispatchRecoveryHostedService>.Instance);
    }
}
