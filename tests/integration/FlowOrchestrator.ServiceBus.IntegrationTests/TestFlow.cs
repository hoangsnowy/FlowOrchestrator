using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// Minimal flow used by integration tests. Has the well-known
/// <see cref="ServiceBusEmulatorFixture.TestFlowId"/> so it matches the subscription
/// pre-provisioned in the emulator's <c>Config.json</c>, and contains a single step
/// of type <c>"TestStep"</c> that the test handler counts.
/// </summary>
internal sealed class TestFlow : IFlowDefinition
{
    public Guid Id { get; } = ServiceBusEmulatorFixture.TestFlowId;
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["only_step"] = new StepMetadata
            {
                Type = "TestStep",
                Inputs = new Dictionary<string, object?> { ["payload"] = "hello" }
            }
        }
    };
}

/// <summary>Counts invocations and signals a TaskCompletionSource so tests can await execution.</summary>
internal sealed class TestStepHandler : IStepHandler
{
    private static int _count;
    private static TaskCompletionSource<int>? _signal;

    /// <summary>Number of times the handler has been invoked since the last <see cref="Reset"/>.</summary>
    public static int InvocationCount => Volatile.Read(ref _count);

    /// <summary>Resets state and returns a fresh task that completes when the next invocation is observed.</summary>
    public static Task<int> ResetAndAwait()
    {
        Volatile.Write(ref _count, 0);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _signal, tcs);
        return tcs.Task;
    }

    /// <summary>Resets the counter without arming a signal.</summary>
    public static void Reset() => Volatile.Write(ref _count, 0);

    /// <inheritdoc/>
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        var n = Interlocked.Increment(ref _count);
        Volatile.Read(ref _signal)?.TrySetResult(n);
        return new ValueTask<object?>(new { ok = true, n });
    }
}
