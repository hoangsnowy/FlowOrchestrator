using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Spins up an in-process test HTTP server with the FlowOrchestrator dashboard mapped.
/// All dependencies are pre-wired with NSubstitute mocks; configure them after construction.
/// </summary>
public sealed class DashboardTestServer : IDisposable
{
    private readonly WebApplication _app;

    public IFlowStore FlowStore { get; } = Substitute.For<IFlowStore>();
    public IFlowRunStore FlowRunStore { get; } = Substitute.For<IFlowRunStore>();
    public IFlowRunControlStore RunControlStore { get; } = Substitute.For<IFlowRunControlStore>();
    public IFlowRunRuntimeStore RuntimeStore { get; } = Substitute.For<IFlowRunRuntimeStore>();
    public IFlowRepository FlowRepository { get; } = Substitute.For<IFlowRepository>();
    public IOutputsRepository OutputsRepository { get; } = Substitute.For<IOutputsRepository>();
    public IFlowEventReader EventReader { get; } = Substitute.For<IFlowEventReader>();
    public IFlowScheduleStateStore ScheduleStateStore { get; } = Substitute.For<IFlowScheduleStateStore>();

    /// <summary>Mock engine used to verify trigger and retry calls from dashboard endpoints.</summary>
    public IFlowOrchestrator FlowOrchestrator { get; } = Substitute.For<IFlowOrchestrator>();

    /// <summary>Mock signal dispatcher used to verify signal endpoint behaviour.</summary>
    public IFlowSignalDispatcher SignalDispatcher { get; } = Substitute.For<IFlowSignalDispatcher>();

    public IRecurringTriggerSync TriggerSync { get; } = Substitute.For<IRecurringTriggerSync>();
    public IRecurringTriggerDispatcher TriggerDispatcher { get; } = Substitute.For<IRecurringTriggerDispatcher>();
    public IRecurringTriggerInspector TriggerInspector { get; } = Substitute.For<IRecurringTriggerInspector>();

    public DashboardTestServer(Action<FlowDashboardOptions>? configureOptions = null)
    {
        // Configure Hangfire in-memory so any background job infrastructure works during tests.
        GlobalConfiguration.Configuration.UseInMemoryStorage();

        ScheduleStateStore.GetAllAsync().Returns(Array.Empty<FlowScheduleState>());
        ScheduleStateStore.GetAsync(Arg.Any<string>()).Returns((FlowScheduleState?)null);
        EventReader.GetRunEventsAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<FlowEventRecord>());
        RunControlStore.GetRunControlAsync(Arg.Any<Guid>()).Returns((FlowRunControlRecord?)null);
        TriggerInspector.GetJobsAsync().Returns(Array.Empty<RecurringTriggerInfo>());
        RuntimeStore.GetStepStatusesAsync(Arg.Any<Guid>())
            .Returns(new Dictionary<string, StepStatus>());

        // Default TriggerAsync returns a non-null object so dashboard can serialize runId from ctx.
        FlowOrchestrator.TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddHangfire(c => c.UseInMemoryStorage());
        builder.Services.AddSingleton(FlowStore);
        builder.Services.AddSingleton(FlowRunStore);
        builder.Services.AddSingleton(RunControlStore);
        builder.Services.AddSingleton(RuntimeStore);
        builder.Services.AddSingleton(FlowRepository);
        builder.Services.AddSingleton(OutputsRepository);
        builder.Services.AddSingleton(EventReader);
        builder.Services.AddSingleton(ScheduleStateStore);
        builder.Services.AddSingleton(FlowOrchestrator);
        builder.Services.AddSingleton(SignalDispatcher);
        builder.Services.AddSingleton(TriggerSync);
        builder.Services.AddSingleton(TriggerDispatcher);
        builder.Services.AddSingleton(TriggerInspector);

        if (configureOptions is not null)
            builder.Services.AddFlowDashboard(configureOptions);
        else
            builder.Services.AddFlowDashboard();

        _app = builder.Build();
        _app.MapFlowDashboard("/flows");
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public HttpClient CreateClient() => _app.GetTestClient();

    public void Dispose()
    {
        _app.StopAsync().GetAwaiter().GetResult();
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
