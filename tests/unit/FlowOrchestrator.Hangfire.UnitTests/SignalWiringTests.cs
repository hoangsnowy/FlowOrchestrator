using System.Reflection;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// Proves the #190 wiring reaches real hosts: <c>AddFlowOrchestrator</c> hands the engine the
/// backend's <see cref="IFlowSignalStore"/> (without it the post-release re-check silently never runs)
/// and hands <see cref="WaitForSignalHandler"/> the configured <c>options.Signals</c>.
/// </summary>
/// <remarks>
/// Both parameters are optional constructor arguments that the container fills only when the service
/// is registered, so a missing registration degrades silently rather than failing to resolve — which
/// is exactly why it needs pinning.
/// </remarks>
public sealed class SignalWiringTests
{
    public static TheoryData<string> Backends() => ["InMemory", "SqlServer"];

    [Theory]
    [MemberData(nameof(Backends))]
    public void The_engine_receives_the_backends_signal_store(string backend)
    {
        // Arrange
        using var provider = Build(backend, _ => { });

        // Act
        var engine = provider.GetRequiredService<IFlowOrchestrator>();
        var wired = typeof(FlowOrchestratorEngine)
            .GetField("_signalStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine);

        // Assert — the same store the signal endpoint writes through.
        Assert.NotNull(wired);
        Assert.Same(provider.GetRequiredService<IFlowSignalStore>(), wired);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task The_handler_parks_for_the_configured_interval(string backend)
    {
        // Arrange — the handler the container builds, with its signal store swapped for a stub so the
        // park needs no database (the options are what is under test, not the store).
        using var provider = Build(
            backend,
            o => o.Signals.IndefiniteParkInterval = TimeSpan.FromSeconds(45),
            services => services.AddSingleton(Substitute.For<IFlowSignalStore>()));
        var handler = provider.GetRequiredService<WaitForSignalHandler>();
        var runId = Guid.NewGuid();

        // Act
        var result = (StepResult)(await handler.ExecuteAsync(
            new Core.Execution.ExecutionContext { RunId = runId },
            Substitute.For<IFlowDefinition>(),
            new TypedStep(runId)))!;

        // Assert
        Assert.Equal(StepStatus.Pending, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(45), result.DelayNextStep);
    }

    private static ServiceProvider Build(
        string backend,
        Action<Core.Configuration.FlowOrchestratorBuilder> configure,
        Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestrator(o =>
        {
            if (backend == "SqlServer")
            {
                // No I/O at construction: the stores only keep the connection string, and the
                // migrator is a hosted service this test never starts.
                o.UseSqlServer("Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true");
            }
            else
            {
                o.UseInMemory();
            }

            o.UseInMemoryRuntime();
            configure(o);
        });
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private sealed class TypedStep(Guid runId) : IStepInstance<WaitForSignalInput>
    {
        public Guid RunId { get; set; } = runId;
        public string? PrincipalId { get; set; }
        public object? TriggerData { get; set; }
        public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
        public string? JobId { get; set; }
        public DateTimeOffset ScheduledTime { get; set; }
        public string Type { get; set; } = WaitForSignalHandler.StepTypeName;
        public string Key => "wait";
        public WaitForSignalInput Inputs { get; set; } = new() { SignalName = "go" };
        public int Index { get; set; }
        public bool ScopeMoveNext { get; set; }
    }
}
