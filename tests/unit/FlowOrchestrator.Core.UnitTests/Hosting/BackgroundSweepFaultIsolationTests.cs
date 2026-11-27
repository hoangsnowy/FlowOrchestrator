using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Hosting;

/// <summary>
/// Fault-isolation coverage for the periodic background sweeps
/// (<see cref="FlowTimeoutEnforcementHostedService"/> and <c>FlowRetentionHostedService</c>).
/// </summary>
/// <remarks>
/// Both sweeps filter their per-tick catch with <c>when (ex is not OperationCanceledException)</c> and only
/// rethrow an <see cref="OperationCanceledException"/> when the STOPPING token is cancelled. An
/// <see cref="OperationCanceledException"/> raised by the store for any other reason (an HTTP-backed store's
/// <see cref="TaskCanceledException"/> on a client timeout, a linked CTS inside a custom store) matches neither
/// catch, escapes <c>ExecuteAsync</c> and ends the loop for the life of the process. The generic host then
/// treats the faulted <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> as fatal under the default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c>. "Will retry on the next tick" — the documented intent
/// of both catch blocks — never happens.
/// </remarks>
public sealed class BackgroundSweepFaultIsolationTests
{
    [Fact]
    public async Task TimeoutSweep_WhenEnforcerThrowsStrayOperationCanceled_KeepsSweepingOnTheNextTick()
    {
        // Arrange — first sweep throws an OCE that is NOT the stopping token; second sweep signals.
        var secondSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var enforcer = Substitute.For<IRunTimeoutEnforcer>();
        enforcer.EnforceDueTimeoutsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new TaskCanceledException("store client timed out");
            }

            secondSweep.TrySetResult();
            return Task.CompletedTask;
        });

        var services = new ServiceCollection().AddSingleton(enforcer).BuildServiceProvider();
        var sut = new FlowTimeoutEnforcementHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FlowRunControlOptions { TimeoutEnforcementInterval = TimeSpan.FromMilliseconds(10) },
            NullLogger<FlowTimeoutEnforcementHostedService>.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.WhenAny(secondSweep.Task, sut.ExecuteTask!).WaitAsync(TimeSpan.FromSeconds(30));
        var loopStatus = sut.ExecuteTask!.Status;
        await sut.StopAsync(CancellationToken.None);

        // Assert — the loop survived the stray OCE and ran another sweep.
        Assert.True(secondSweep.Task.IsCompleted,
            $"Timeout sweep loop ended after a stray OperationCanceledException (ExecuteTask status: {loopStatus}); no further sweep ran.");
    }

    [Fact]
    public async Task RetentionSweep_WhenStoreThrowsStrayOperationCanceled_KeepsSweepingOnTheNextTick()
    {
        // Arrange — first CleanupAsync throws an OCE that is NOT the stopping token; second one signals.
        var secondSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var store = Substitute.For<IFlowRetentionStore>();
        store.CleanupAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new TaskCanceledException("store client timed out");
            }

            secondSweep.TrySetResult();
            return Task.CompletedTask;
        });

        var sut = new FlowRetentionHostedService(
            [store],
            new FlowRetentionOptions { Enabled = true, SweepInterval = TimeSpan.FromMilliseconds(10) },
            NullLogger<FlowRetentionHostedService>.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.WhenAny(secondSweep.Task, sut.ExecuteTask!).WaitAsync(TimeSpan.FromSeconds(30));
        var loopStatus = sut.ExecuteTask!.Status;
        await sut.StopAsync(CancellationToken.None);

        // Assert — the loop survived the stray OCE and ran another sweep.
        Assert.True(secondSweep.Task.IsCompleted,
            $"Retention sweep loop ended after a stray OperationCanceledException (ExecuteTask status: {loopStatus}); no further sweep ran.");
    }

    [Fact]
    public async Task RetentionSweep_CancelledByTheStoppingToken_EndsWithoutFaulting()
    {
        // Arrange — the store honours the token it is given: it blocks until shutdown cancels it.
        var sweepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = Substitute.For<IFlowRetentionStore>();
        store.CleanupAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            sweepStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
        });

        var sut = new FlowRetentionHostedService(
            [store],
            new FlowRetentionOptions { Enabled = true, SweepInterval = TimeSpan.FromHours(1) },
            NullLogger<FlowRetentionHostedService>.Instance);
        await sut.StartAsync(CancellationToken.None);
        await sweepStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Act
        await sut.StopAsync(CancellationToken.None);

        // Assert — a real shutdown is still a clean exit, not a fault.
        Assert.False(sut.ExecuteTask!.IsFaulted, $"ExecuteTask faulted on shutdown: {sut.ExecuteTask.Exception}");
    }
}
