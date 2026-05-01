using System.Diagnostics;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Hangfire.Telemetry;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>Marker collection that serializes Hangfire global-filter tests; <see cref="GlobalJobFilters"/> is process-wide mutable state.</summary>
[CollectionDefinition(nameof(HangfireIntegrationGlobalFiltersCollection), DisableParallelization = true)]
public sealed class HangfireIntegrationGlobalFiltersCollection
{
}

/// <summary>
/// End-to-end test for the W3C trace-context propagation across the Hangfire enqueue/dequeue boundary.
/// Spins up a real <see cref="BackgroundJobServer"/> over Hangfire.InMemory storage so the filter is
/// exercised exactly as it would be in production.
/// </summary>
[Collection(nameof(HangfireIntegrationGlobalFiltersCollection))]
public sealed class TraceContextPropagationTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly BackgroundJobServer _server;

    private readonly JobStorage _storage;

    public TraceContextPropagationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));
        services.AddTransient<TestJob>();

        // Mirror what AddFlowOrchestrator(...).UseHangfire() does for the trace filter — we test
        // the filter directly here without dragging the whole engine into the integration surface.
        if (!GlobalJobFilters.Filters.Any(f => f.Instance is TraceContextHangfireFilter))
        {
            GlobalJobFilters.Filters.Add(new TraceContextHangfireFilter());
        }

        _provider = services.BuildServiceProvider();

        // Build the in-memory storage directly so the test does not race against the static
        // JobStorage.Current setter that AddHangfire would otherwise mutate from a hosted-service.
        _storage = new InMemoryStorage();
        JobStorage.Current = _storage;

        // Start a server with one worker so the test drains predictably.
        _server = new BackgroundJobServer(
            new BackgroundJobServerOptions
            {
                WorkerCount = 1,
                ServerName = "trace-test-server",
                Activator = new ServiceProviderJobActivator(_provider),
            },
            _storage);
    }

    public void Dispose()
    {
        _server.Dispose();
        _provider.Dispose();
        GlobalJobFilters.Filters
            .Where(f => f.Instance is TraceContextHangfireFilter)
            .Select(f => f.Instance)
            .ToList()
            .ForEach(GlobalJobFilters.Filters.Remove);
    }

    [Fact]
    public async Task EnqueueAndExecute_RestoresParentTraceContextOnTheWorker()
    {
        // Arrange
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == FlowOrchestratorTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using var parentSource = new ActivitySource("FlowOrchestrator.Hangfire.Tests.Parent");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "FlowOrchestrator.Hangfire.Tests.Parent",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(parentListener);

        var client = new BackgroundJobClient(_storage);
        TestJob.Reset();

        string expectedTraceId;
        using (var parent = parentSource.StartActivity("test.parent"))
        {
            Assert.NotNull(parent);
            expectedTraceId = parent.TraceId.ToString();
            client.Enqueue<TestJob>(j => j.Run());
        }

        // Act — wait for the worker to pick up + run + finish
        var ran = await TestJob.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.True(ran, "TestJob did not run within 30s — Hangfire server may not have started.");
        var wrapper = Assert.Single(captured, a => a.OperationName == "flow.runtime.execute");
        Assert.Equal(expectedTraceId, wrapper.TraceId.ToString());
        Assert.Equal("hangfire", wrapper.GetTagItem("messaging.system"));
    }

    [Fact]
    public async Task EnqueueWithoutParentActivity_DoesNotOpenWrapperActivity()
    {
        // Arrange
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == FlowOrchestratorTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var client = new BackgroundJobClient(_storage);
        TestJob.Reset();

        // Act — no parent activity in scope; enqueue + drain
        Assert.Null(Activity.Current);
        client.Enqueue<TestJob>(j => j.Run());
        var ran = await TestJob.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.True(ran);
        Assert.DoesNotContain(captured, a => a.OperationName == "flow.runtime.execute");
    }

    /// <summary>Marker job that signals completion via a TaskCompletionSource so tests can await it deterministically.</summary>
    public sealed class TestJob
    {
        private static TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset()
        {
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static Task<bool> WaitForCompletionAsync(TimeSpan timeout)
        {
            return Task.Run(async () =>
            {
                var winner = await Task.WhenAny(_tcs.Task, Task.Delay(timeout));
                return winner == _tcs.Task;
            });
        }

        public void Run()
        {
            _tcs.TrySetResult();
        }
    }

    private sealed class ServiceProviderJobActivator : JobActivator
    {
        private readonly IServiceProvider _services;
        public ServiceProviderJobActivator(IServiceProvider services) => _services = services;
        public override object ActivateJob(Type jobType) => _services.GetRequiredService(jobType);
    }
}
