using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
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
    public IFlowRepository FlowRepository { get; } = Substitute.For<IFlowRepository>();
    public IOutputsRepository OutputsRepository { get; } = Substitute.For<IOutputsRepository>();
    public IFlowEventReader EventReader { get; } = Substitute.For<IFlowEventReader>();
    public IFlowScheduleStateStore ScheduleStateStore { get; } = Substitute.For<IFlowScheduleStateStore>();
    public IHangfireFlowTrigger FlowTrigger { get; } = Substitute.For<IHangfireFlowTrigger>();
    public IRecurringTriggerSync TriggerSync { get; } = Substitute.For<IRecurringTriggerSync>();
    public IRecurringJobManager JobManager { get; } = Substitute.For<IRecurringJobManager>();

    public DashboardTestServer(Action<FlowDashboardOptions>? configureOptions = null)
    {
        // Configure Hangfire in-memory so BackgroundJob.Enqueue works during tests.
        GlobalConfiguration.Configuration.UseInMemoryStorage();

        ScheduleStateStore.GetAllAsync().Returns(Array.Empty<FlowScheduleState>());
        ScheduleStateStore.GetAsync(Arg.Any<string>()).Returns((FlowScheduleState?)null);
        EventReader.GetRunEventsAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<FlowEventRecord>());
        RunControlStore.GetRunControlAsync(Arg.Any<Guid>()).Returns((FlowRunControlRecord?)null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddHangfire(c => c.UseInMemoryStorage());
        builder.Services.AddSingleton(FlowStore);
        builder.Services.AddSingleton(FlowRunStore);
        builder.Services.AddSingleton(RunControlStore);
        builder.Services.AddSingleton(FlowRepository);
        builder.Services.AddSingleton(OutputsRepository);
        builder.Services.AddSingleton(EventReader);
        builder.Services.AddSingleton(ScheduleStateStore);
        builder.Services.AddSingleton(FlowTrigger);
        builder.Services.AddSingleton(TriggerSync);
        builder.Services.AddSingleton(JobManager);

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
