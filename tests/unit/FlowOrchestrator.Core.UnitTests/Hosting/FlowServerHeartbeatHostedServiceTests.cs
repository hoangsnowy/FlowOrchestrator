using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlowOrchestrator.Core.Tests.Hosting;

/// <summary>
/// <see cref="FlowServerHeartbeatHostedService"/>: registers on start, heartbeats on the interval, purges, removes
/// itself on stop, and does nothing without a store. Time is driven by <see cref="FakeTimeProvider"/>; every wait
/// is on a store call landing, never on the wall clock.
/// </summary>
public sealed class FlowServerHeartbeatHostedServiceTests : IAsyncDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingServerStore _store = new();
    private readonly StepLeaseKeeper _keeper = new(new FlowRunControlOptions());
    private readonly FlowServerOptions _options = new();
    private ServiceProvider? _services;
    private FlowServerHeartbeatHostedService? _service;

    public async ValueTask DisposeAsync()
    {
        _service?.Dispose();
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_registers_the_server_under_the_lease_owner_id()
    {
        // Arrange
        var service = CreateService(flows: TwoFlows());
        using var lease = _keeper.Track(new StepLease(Guid.NewGuid(), "a", 1));

        // Act
        await service.StartAsync(CancellationToken.None);
        var heartbeat = await _store.NextUpsertAsync();

        // Assert
        Assert.Equal(_keeper.OwnerId, heartbeat.ServerId);
        Assert.Equal(Environment.MachineName, heartbeat.MachineName);
        Assert.Equal(Environment.ProcessId, heartbeat.ProcessId);
        Assert.Equal("Recording", heartbeat.Runtime);
        Assert.Equal(1, heartbeat.ActiveStepCount);
        Assert.Equal(2, heartbeat.FlowCount);
        Assert.False(string.IsNullOrEmpty(heartbeat.Version));
    }

    [Fact]
    public async Task Heartbeats_again_each_time_the_interval_elapses()
    {
        // Arrange
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await _store.NextUpsertAsync();

        // Act
        _time.Advance(_options.HeartbeatInterval);
        var second = await _store.NextUpsertAsync();
        _time.Advance(_options.HeartbeatInterval);
        var third = await _store.NextUpsertAsync();

        // Assert
        Assert.Equal(_keeper.OwnerId, second.ServerId);
        Assert.Equal(_keeper.OwnerId, third.ServerId);
    }

    [Fact]
    public async Task Configured_runtime_name_wins_over_the_dispatcher_type()
    {
        // Arrange
        _options.RuntimeName = "  Custom  ";
        var service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);
        var heartbeat = await _store.NextUpsertAsync();

        // Assert
        Assert.Equal("Custom", heartbeat.Runtime);
    }

    [Fact]
    public async Task A_failed_heartbeat_is_retried_on_the_next_tick()
    {
        // Arrange
        _store.FailNextUpsert = new InvalidOperationException("store down");
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await _store.NextUpsertAttemptAsync();

        // Act
        _time.Advance(_options.HeartbeatInterval);
        var heartbeat = await _store.NextUpsertAsync();

        // Assert
        Assert.Equal(_keeper.OwnerId, heartbeat.ServerId);
    }

    [Fact]
    public async Task Purges_on_start_and_again_after_the_purge_cadence()
    {
        // Arrange
        _options.PurgeAfter = TimeSpan.FromMinutes(2); // a quarter = 30 s = two 15 s heartbeats
        var service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);
        await _store.NextUpsertAsync();
        var first = await _store.NextPurgeAsync();
        _time.Advance(_options.HeartbeatInterval);
        await _store.NextUpsertAsync();
        _time.Advance(_options.HeartbeatInterval);
        await _store.NextUpsertAsync();
        var second = await _store.NextPurgeAsync();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(2), first);
        Assert.Equal(TimeSpan.FromMinutes(2), second);
        Assert.Equal(3, _store.UpsertCount);
    }

    [Fact]
    public async Task StopAsync_removes_the_server_row()
    {
        // Arrange
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        await _store.NextUpsertAsync();

        // Act
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal([_keeper.OwnerId], _store.Removed);
    }

    [Fact]
    public async Task StopAsync_never_throws_when_the_removal_fails()
    {
        // Arrange
        var store = Substitute.For<IFlowServerStore>();
        var upserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.UpsertServerAsync(Arg.Any<FlowServerRecord>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                upserted.TrySetResult();
                return Task.CompletedTask;
            });
        store.RemoveServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("gone"));
        var service = CreateService(store: store);
        await service.StartAsync(CancellationToken.None);
        await upserted.Task.WaitAsync(Budget);

        // Act
        var stop = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));

        // Assert
        Assert.Null(stop);
        await store.Received(1).RemoveServerAsync(_keeper.OwnerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_server_store_the_service_returns_at_once_and_stop_writes_nothing()
    {
        // Arrange
        var service = CreateService(registerStore: false);

        // Act
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Budget);
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(0, _store.UpsertCount);
    }

    [Fact]
    public async Task Disabled_registry_writes_nothing()
    {
        // Arrange
        _options.Enabled = false;
        var service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Budget);
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, _store.UpsertCount);
        Assert.Empty(_store.Removed);
    }

    [Theory]
    [InlineData(15, 3600, 40)]   // default: a quarter hour capped at 10 min = 40 beats
    [InlineData(15, 120, 2)]     // 30 s / 15 s
    [InlineData(15, 40, 1)]      // a quarter below one interval still purges every beat
    [InlineData(600, 86400, 1)]  // the 10 min cap equals one interval
    public void BeatsPerPurge_is_a_quarter_of_the_purge_age_clamped(int intervalSeconds, int purgeSeconds, int expected)
    {
        // Arrange
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var purgeAfter = TimeSpan.FromSeconds(purgeSeconds);

        // Act
        var beats = FlowServerHeartbeatHostedService.BeatsPerPurge(interval, purgeAfter);

        // Assert
        Assert.Equal(expected, beats);
    }

    [Theory]
    [InlineData(typeof(RecordingStepDispatcher), "Recording")]
    [InlineData(typeof(string), "String")]
    [InlineData(null, "Unknown")]
    public void RuntimeNameFor_strips_the_dispatcher_suffix(Type? dispatcherType, string expected)
    {
        // Arrange

        // Act
        var name = FlowServerHeartbeatHostedService.RuntimeNameFor(dispatcherType);

        // Assert
        Assert.Equal(expected, name);
    }

    private FlowServerHeartbeatHostedService CreateService(FlowRegistry? flows = null, IFlowServerStore? store = null, bool registerStore = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStepDispatcher, RecordingStepDispatcher>();
        if (registerStore)
        {
            services.AddSingleton(store ?? _store);
        }

        _services = services.BuildServiceProvider();
        _service = new FlowServerHeartbeatHostedService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _keeper,
            _options,
            flows ?? FlowRegistry.Empty,
            NullLogger<FlowServerHeartbeatHostedService>.Instance,
            _time);
        return _service;
    }

    private static FlowRegistry TwoFlows()
    {
        var a = Substitute.For<IFlowDefinition>();
        a.Id.Returns(Guid.NewGuid());
        var b = Substitute.For<IFlowDefinition>();
        b.Id.Returns(Guid.NewGuid());
        return new FlowRegistry([a, b]);
    }

    /// <summary>Dispatcher whose type name the runtime name is derived from; never called.</summary>
    private sealed class RecordingStepDispatcher : IStepDispatcher
    {
        public ValueTask<string?> EnqueueStepAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<string?> ScheduleStepAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step, TimeSpan delay, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Server store that publishes every call on a channel, so a test awaits the call itself.</summary>
    private sealed class RecordingServerStore : IFlowServerStore
    {
        private readonly Channel<FlowServerRecord?> _upserts = Channel.CreateUnbounded<FlowServerRecord?>();
        private readonly Channel<TimeSpan> _purges = Channel.CreateUnbounded<TimeSpan>();
        private int _upsertCount;

        public Exception? FailNextUpsert { get; set; }

        public int UpsertCount => Volatile.Read(ref _upsertCount);

        public List<string> Removed { get; } = [];

        public Task UpsertServerAsync(FlowServerRecord server, CancellationToken cancellationToken = default)
        {
            if (FailNextUpsert is { } fault)
            {
                FailNextUpsert = null;
                _upserts.Writer.TryWrite(null);
                return Task.FromException(fault);
            }

            Interlocked.Increment(ref _upsertCount);
            _upserts.Writer.TryWrite(server);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FlowServerRecord>> GetServersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FlowServerRecord>>([]);

        public Task<bool> RemoveServerAsync(string serverId, CancellationToken cancellationToken = default)
        {
            lock (Removed)
            {
                Removed.Add(serverId);
            }

            return Task.FromResult(true);
        }

        public Task<int> PurgeServersAsync(TimeSpan notSeenFor, CancellationToken cancellationToken = default)
        {
            _purges.Writer.TryWrite(notSeenFor);
            return Task.FromResult(0);
        }

        /// <summary>Waits for the next successful upsert, skipping failed attempts.</summary>
        public async Task<FlowServerRecord> NextUpsertAsync()
        {
            while (true)
            {
                if (await _upserts.Reader.ReadAsync().AsTask().WaitAsync(Budget) is { } server)
                {
                    return server;
                }
            }
        }

        /// <summary>Waits for the next upsert attempt, failed or not.</summary>
        public Task NextUpsertAttemptAsync() => _upserts.Reader.ReadAsync().AsTask().WaitAsync(Budget);

        public Task<TimeSpan> NextPurgeAsync() => _purges.Reader.ReadAsync().AsTask().WaitAsync(Budget);
    }
}
