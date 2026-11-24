using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Hosting.Internal;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlowOrchestrator.Core.Tests.Hosting;

/// <summary>
/// <see cref="FlowUnsentDispatchRecoveryHostedService"/> and the per-pass <see cref="UnsentDispatchRecoverer"/>:
/// what is re-delivered, with which token, how failures are isolated, and when the sweep is off.
/// </summary>
public sealed class FlowUnsentDispatchRecoveryHostedServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly IFlowRunStore _store = Substitute.For<IFlowRunStore>();
    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IOutputsRepository _outputs = Substitute.For<IOutputsRepository>();
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly IFlowDefinition _flow;

    /// <summary>Registers one flow declaring steps <c>a</c> and <c>b</c>; the dispatcher accepts every step.</summary>
    public FlowUnsentDispatchRecoveryHostedServiceTests()
    {
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _flow = Substitute.For<IFlowDefinition>();
        _flow.Id.Returns(Guid.NewGuid());
        _flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "Work", Inputs = new Dictionary<string, object?> { ["x"] = 1 } },
                ["b"] = new StepMetadata { Type = "Other" }
            }
        });
        _dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<string?>("job-" + call.ArgAt<IStepInstance>(2).Key));
    }

    [Fact]
    public async Task A_leased_row_is_enqueued_with_its_manifest_type_and_inputs_and_annotated()
    {
        // Arrange
        var runId = Guid.NewGuid();
        Lease([new UnsentDispatch(runId, _flow.Id, "a")]);
        _outputs.GetTriggerDataAsync(runId).Returns(new ValueTask<object?>("payload"));

        // Act
        var count = await CreateRecoverer().RecoverAsync(Timeout, CancellationToken.None);

        // Assert
        Assert.Equal(1, count);
        await _dispatcher.Received(1).EnqueueStepAsync(
            Arg.Is<IExecutionContext>(c => c.RunId == runId && Equals(c.TriggerData, "payload")),
            _flow,
            Arg.Is<IStepInstance>(s => s.Key == "a" && s.Type == "Work" && Equals(s.Inputs["x"], 1) && s.RunId == runId),
            CancellationToken.None);
        await _store.Received(1).AnnotateDispatchAsync(runId, "a", "job-a", CancellationToken.None);
        await _store.Received(1).LeaseUnsentDispatchesAsync(Timeout, UnsentDispatchRecoverer.BatchSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_row_for_an_unregistered_flow_or_an_undeclared_step_is_left_alone()
    {
        // Arrange
        Lease([new UnsentDispatch(Guid.NewGuid(), Guid.NewGuid(), "a"), new UnsentDispatch(Guid.NewGuid(), _flow.Id, "gone")]);

        // Act
        var count = await CreateRecoverer().RecoverAsync(Timeout, CancellationToken.None);

        // Assert
        Assert.Equal(0, count);
        Assert.Empty(_dispatcher.ReceivedCalls());
        Assert.Equal(2, LoggedEventIds().Count(id => id == 5042));
    }

    [Fact]
    public async Task A_failed_redelivery_is_logged_and_the_next_row_is_still_redelivered()
    {
        // Arrange — the first enqueue throws the adapter's own cancellation; it must not end the pass.
        var first = new UnsentDispatch(Guid.NewGuid(), _flow.Id, "a");
        var second = new UnsentDispatch(Guid.NewGuid(), _flow.Id, "b");
        Lease([first, second]);
        _dispatcher.EnqueueStepAsync(
                Arg.Is<IExecutionContext>(c => c.RunId == first.RunId), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => throw new TaskCanceledException("send timed out"));

        // Act
        var count = await CreateRecoverer().RecoverAsync(Timeout, CancellationToken.None);

        // Assert
        Assert.Equal(1, count);
        await _store.DidNotReceive().AnnotateDispatchAsync(first.RunId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.Received(1).AnnotateDispatchAsync(second.RunId, "b", "job-b", CancellationToken.None);
        Assert.Contains(5041, LoggedEventIds());
    }

    [Fact]
    public async Task A_failed_annotation_is_logged_and_still_counts_as_redelivered()
    {
        // Arrange
        var row = new UnsentDispatch(Guid.NewGuid(), _flow.Id, "a");
        Lease([row]);
        _store.AnnotateDispatchAsync(row.RunId, "a", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db blip"));

        // Act
        var count = await CreateRecoverer().RecoverAsync(Timeout, CancellationToken.None);

        // Assert
        Assert.Equal(1, count);
        Assert.Contains(5043, LoggedEventIds());
    }

    [Fact]
    public async Task Trigger_data_is_read_once_per_run_however_many_of_its_rows_are_leased()
    {
        // Arrange
        var runId = Guid.NewGuid();
        Lease([new UnsentDispatch(runId, _flow.Id, "a"), new UnsentDispatch(runId, _flow.Id, "b")]);

        // Act
        var count = await CreateRecoverer().RecoverAsync(Timeout, CancellationToken.None);

        // Assert
        Assert.Equal(2, count);
        await _outputs.Received(1).GetTriggerDataAsync(runId);
    }

    [Fact]
    public async Task A_full_batch_makes_the_pass_lease_again_and_a_short_one_ends_it()
    {
        // Arrange
        var full = Enumerable.Range(0, UnsentDispatchRecoverer.BatchSize)
            .Select(_ => new UnsentDispatch(Guid.NewGuid(), _flow.Id, "a"))
            .ToList();
        _store.LeaseUnsentDispatchesAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UnsentDispatch>>(full), Task.FromResult<IReadOnlyList<UnsentDispatch>>([]));

        // Act
        var count = await CreateRecoverer().RecoverAsync(Timeout, CancellationToken.None);

        // Assert
        Assert.Equal(UnsentDispatchRecoverer.BatchSize, count);
        await _store.Received(2).LeaseUnsentDispatchesAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(500, 1000)]
    [InlineData(60_000, 30_000)]
    [InlineData(30_000, 15_000)]
    [InlineData(600_000, 30_000)]
    public void The_sweep_interval_is_half_the_timeout_clamped_to_one_to_thirty_seconds(int timeoutMs, int expectedMs)
    {
        // Arrange
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);

        // Act
        var interval = FlowUnsentDispatchRecoveryHostedService.SweepIntervalFor(timeout);

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), interval);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_null_or_non_positive_timeout_turns_the_sweep_off(int? timeoutSeconds)
    {
        // Arrange
        var sut = CreateService(timeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await sut.StopAsync(CancellationToken.None);

        // Assert
        Assert.Empty(_store.ReceivedCalls());
    }

    [Fact]
    public async Task The_sweep_runs_at_startup_and_survives_a_failing_pass()
    {
        // Arrange — the startup pass throws a store-side cancellation (not the stopping token); the loop must
        // keep sweeping, as the timeout sweep does.
        var secondPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _store.LeaseUnsentDispatchesAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new TaskCanceledException("store client timed out");
                }

                secondPass.TrySetResult();
                return Task.FromResult<IReadOnlyList<UnsentDispatch>>([]);
            });
        var sut = CreateService(TimeSpan.FromMilliseconds(10));

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.WhenAny(secondPass.Task, sut.ExecuteTask!).WaitAsync(TimeSpan.FromSeconds(30));
        var loopStatus = sut.ExecuteTask!.Status;
        await sut.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(secondPass.Task.IsCompleted, $"The sweep loop ended after a failing pass (ExecuteTask status: {loopStatus}).");
    }

    private void Lease(IReadOnlyList<UnsentDispatch> rows) =>
        _store.LeaseUnsentDispatchesAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rows), Task.FromResult<IReadOnlyList<UnsentDispatch>>([]));

    private UnsentDispatchRecoverer CreateRecoverer() =>
        new(_store, new FlowRegistry([_flow]), _dispatcher, _outputs, _logger);

    private FlowUnsentDispatchRecoveryHostedService CreateService(TimeSpan? timeout)
    {
        var services = new ServiceCollection()
            .AddSingleton(_store)
            .AddSingleton(new FlowRegistry([_flow]))
            .AddSingleton(_dispatcher)
            .AddSingleton(_outputs)
            .BuildServiceProvider();
        return new FlowUnsentDispatchRecoveryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FlowRunControlOptions { UnsentDispatchTimeout = timeout },
            NullLogger<FlowUnsentDispatchRecoveryHostedService>.Instance);
    }

    private IEnumerable<int> LoggedEventIds() =>
        _logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(c => ((EventId)c.GetArguments()[1]!).Id);
}
