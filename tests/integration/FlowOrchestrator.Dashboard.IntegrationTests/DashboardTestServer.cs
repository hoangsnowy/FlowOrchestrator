using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Notifications;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Spins up an in-process test HTTP server with the FlowOrchestrator dashboard mapped.
/// All dependencies are pre-wired with NSubstitute mocks; configure them after construction.
/// </summary>
/// <remarks>
/// The host runs in the <see cref="Environments.Development"/> environment unless a test asks for another one
/// (<see cref="InEnvironment"/>, <see cref="WithServices"/>): an unprotected dashboard grants anonymous
/// callers operator access only in Development, which is what the endpoint tests exercise. Tests for the
/// non-Development default pass an explicit environment.
/// </remarks>
public sealed class DashboardTestServer : IDisposable
{
    private readonly WebApplication _app;

    public IFlowStore FlowStore { get; } = Substitute.For<IFlowStore>();

    /// <summary>
    /// Fake run store. <see cref="IFlowRunStore.GetRunAsync"/> is pre-wired to forward to
    /// <see cref="IFlowRunStore.GetRunDetailAsync"/>, mirroring the interface's own default
    /// implementation.
    /// </summary>
    /// <remarks>
    /// NSubstitute proxies intercept default interface methods rather than running their bodies, so
    /// without this every test that arranges <c>GetRunDetailAsync</c> would silently get
    /// <see langword="null"/> back from an endpoint that reads the header only — surfacing as a 404
    /// across unrelated tests. Arranging <c>GetRunAsync</c> explicitly in a test still overrides this.
    /// </remarks>
    public IFlowRunStore FlowRunStore { get; } = CreateRunStore();

    private static IFlowRunStore CreateRunStore()
    {
        var store = Substitute.For<IFlowRunStore>();
        store.GetRunAsync(Arg.Any<Guid>())
             .Returns(call => store.GetRunDetailAsync(call.Arg<Guid>()));
        // Same reason: SearchRunsAsync is a default interface method, so the proxy would hand the
        // run-list endpoint a null page. Tests that care arrange it explicitly.
        store.SearchRunsAsync(Arg.Any<RunSearchQuery>(), Arg.Any<CancellationToken>())
             .Returns(new RunSearchPage(Array.Empty<FlowRunRecord>(), false, null));
        return store;
    }
    public IFlowRunControlStore RunControlStore { get; } = Substitute.For<IFlowRunControlStore>();
    public IFlowRunRuntimeStore RuntimeStore { get; } = Substitute.For<IFlowRunRuntimeStore>();
    /// <summary>
    /// Flows the endpoints resolve through <see cref="FlowRegistry"/>. Settable per test: the
    /// registry is registered as a transient factory, so each request sees the current value.
    /// </summary>
    public FlowRegistry Flows { get; set; } = FlowRegistry.Empty;
    public IOutputsRepository OutputsRepository { get; } = Substitute.For<IOutputsRepository>();
    public IFlowScheduleStateStore ScheduleStateStore { get; } = Substitute.For<IFlowScheduleStateStore>();

    /// <summary>Mock engine used to verify trigger and retry calls from dashboard endpoints.</summary>
    public IFlowOrchestrator FlowOrchestrator { get; } = Substitute.For<IFlowOrchestrator>();

    /// <summary>Mock signal dispatcher used to verify signal endpoint behaviour.</summary>
    public IFlowSignalDispatcher SignalDispatcher { get; } = Substitute.For<IFlowSignalDispatcher>();

    public IRecurringTriggerSync TriggerSync { get; } = Substitute.For<IRecurringTriggerSync>();
    public IRecurringTriggerDispatcher TriggerDispatcher { get; } = Substitute.For<IRecurringTriggerDispatcher>();
    public IRecurringTriggerInspector TriggerInspector { get; } = Substitute.For<IRecurringTriggerInspector>();

    /// <summary>Spins up the dashboard with optional inline option configuration via the <see cref="Action{T}"/> overload.</summary>
    public DashboardTestServer(Action<FlowDashboardOptions>? configureOptions = null)
        : this(services =>
        {
            if (configureOptions is not null)
                services.AddFlowDashboard(configureOptions);
            else
                services.AddFlowDashboard();
        }, Environments.Development)
    {
    }

    /// <summary>
    /// Spins up the dashboard via the <c>AddFlowDashboard(IConfiguration, Action&lt;FlowDashboardOptions&gt;)</c>
    /// overload, exercising the config-bind-then-delegate registration path.
    /// </summary>
    /// <param name="configuration">Configuration bound into <see cref="FlowDashboardOptions"/> (e.g. Basic Auth).</param>
    /// <param name="configureOptions">Delegate applied after binding, for code-only settings.</param>
    public DashboardTestServer(IConfiguration configuration, Action<FlowDashboardOptions> configureOptions)
        : this(services => services.AddFlowDashboard(configuration, configureOptions), Environments.Development)
    {
    }

    /// <summary>
    /// Spins up the dashboard with full control over its registration, e.g. to add an authentication
    /// scheme. <paramref name="registerDashboard"/> must call <c>AddFlowDashboard</c> itself.
    /// </summary>
    /// <param name="registerDashboard">Registers the dashboard and any extra services.</param>
    /// <returns>The started server.</returns>
    /// <param name="environmentName">Host environment; defaults to <see cref="Environments.Development"/>.</param>
    public static DashboardTestServer WithServices(Action<IServiceCollection> registerDashboard, string? environmentName = null)
        => new(registerDashboard, environmentName ?? Environments.Development);

    /// <summary>Spins up the dashboard in the given host environment, e.g. <see cref="Environments.Production"/>.</summary>
    /// <param name="environmentName">Host environment name.</param>
    /// <param name="configureOptions">Optional dashboard option configuration.</param>
    /// <returns>The started server.</returns>
    public static DashboardTestServer InEnvironment(string environmentName, Action<FlowDashboardOptions>? configureOptions = null)
        => new(services =>
        {
            if (configureOptions is not null)
                services.AddFlowDashboard(configureOptions);
            else
                services.AddFlowDashboard();
        }, environmentName);

    private DashboardTestServer(Action<IServiceCollection> registerDashboard, string environmentName)
    {
        // Configure Hangfire in-memory so any background job infrastructure works during tests.
        GlobalConfiguration.Configuration.UseInMemoryStorage();

        ScheduleStateStore.GetAllAsync().Returns(Array.Empty<FlowScheduleState>());
        ScheduleStateStore.GetAsync(Arg.Any<string>()).Returns(default(FlowScheduleState?));
        RunControlStore.GetRunControlAsync(Arg.Any<Guid>()).Returns(default(FlowRunControlRecord?));
        TriggerInspector.GetJobsAsync().Returns(Array.Empty<RecurringTriggerInfo>());
        RuntimeStore.GetStepStatusesAsync(Arg.Any<Guid>())
            .Returns(new Dictionary<string, StepStatus>());

        // Default TriggerAsync returns a non-null object so dashboard can serialize runId from ctx.
        FlowOrchestrator.TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddHangfire(c => c.UseInMemoryStorage());
        builder.Services.AddSingleton(FlowStore);
        builder.Services.AddSingleton(FlowRunStore);
        builder.Services.AddSingleton(RunControlStore);
        builder.Services.AddSingleton(RuntimeStore);
        builder.Services.AddTransient(_ => Flows);
        builder.Services.AddSingleton(OutputsRepository);
        builder.Services.AddSingleton(ScheduleStateStore);
        builder.Services.AddSingleton(FlowOrchestrator);
        builder.Services.AddSingleton(SignalDispatcher);
        builder.Services.AddSingleton(TriggerSync);
        builder.Services.AddSingleton(TriggerDispatcher);
        builder.Services.AddSingleton(TriggerInspector);

        registerDashboard(builder.Services);

        _app = builder.Build();
        _app.MapFlowDashboard("/flows");
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public HttpClient CreateClient() => _app.GetTestClient();

    /// <summary>Lazily-created reusable HTTP client for tests that don't need a fresh per-call instance.</summary>
    public HttpClient Client => _client ??= _app.GetTestClient();
    private HttpClient? _client;

    /// <summary>Resolved DI service provider for tests that need to reach into the registered services.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// Resolved SSE broadcaster — tests use this to publish events into the dashboard's
    /// realtime channel without going through the engine. Registered by <c>AddFlowDashboard()</c>.
    /// </summary>
    public SseFlowEventBroadcaster Broadcaster => _app.Services.GetRequiredService<SseFlowEventBroadcaster>();

    public void Dispose()
    {
        _app.StopAsync().GetAwaiter().GetResult();
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
