using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Single-step polling flow with a 30-second manifest interval — without
/// <see cref="FlowTestHostBuilder{TFlow}.WithFastPolling"/> the test would take ~60s.
/// </summary>
public sealed class PollingFlow : IFlowDefinition
{
    public Guid Id { get; } = new("44444444-4444-4444-4444-444444444444");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["wait_for_ready"] = new StepMetadata
            {
                Type = "PollReady",
                Inputs = new Dictionary<string, object?>
                {
                    ["pollEnabled"] = true,
                    ["pollIntervalSeconds"] = 30,
                    ["pollTimeoutSeconds"] = 600,
                    ["pollMinAttempts"] = 1,
                    ["pollConditionPath"] = "status",
                    ["pollConditionEquals"] = "ready"
                }
            }
        }
    };
}

/// <summary>Inputs for <see cref="PollReadyStepHandler"/> — implements <see cref="IPollableInput"/>.</summary>
public sealed class PollReadyInput : IPollableInput
{
    public bool PollEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 30;
    public int PollTimeoutSeconds { get; set; } = 600;
    public int PollMinAttempts { get; set; } = 1;
    public string? PollConditionPath { get; set; }
    public object? PollConditionEquals { get; set; }
    public string? PollStartedAtUtc { get; set; }
    public int? PollAttempt { get; set; }
}

/// <summary>
/// Polling handler that returns <c>{"status":"pending"}</c> for the first two attempts
/// and <c>{"status":"ready"}</c> on the third — exercising the polling reschedule loop.
/// </summary>
public sealed class PollReadyStepHandler : PollableStepHandler<PollReadyInput>
{
    private readonly PollAttemptCounter _counter;
    public PollReadyStepHandler(PollAttemptCounter counter) => _counter = counter;

    protected override ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext ctx, IFlowDefinition flow, IStepInstance<PollReadyInput> step)
    {
        var attempt = _counter.Increment();
        var status = attempt >= 3 ? "ready" : "pending";
        var element = JsonSerializer.SerializeToElement(new { status });
        return ValueTask.FromResult((element, true));
    }
}

/// <summary>Singleton counter shared across <see cref="PollReadyStepHandler"/> instances to drive deterministic polling.</summary>
public sealed class PollAttemptCounter
{
    private int _attempts;
    public int Increment() => Interlocked.Increment(ref _attempts);
}
