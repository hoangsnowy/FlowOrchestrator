using System.Collections.Concurrent;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.StorageMigration.IntegrationTests;

/// <summary>
/// <c>start</c> → <c>items</c> (ForEach over <c>@triggerBody()?.items</c>, two at a time) → <c>tail</c>; and
/// <c>gated</c>, which a When clause skips unless <c>amount &gt; 1000</c>.
/// </summary>
public sealed class OrderFlow : IFlowDefinition
{
    public Guid Id { get; } = new("5a000000-0000-0000-0000-000000000001");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
        Steps = new StepCollection
        {
            ["start"] = new StepMetadata
            {
                Type = "Echo",
                Inputs = new Dictionary<string, object?> { ["label"] = "@triggerBody()?.label", ["payload"] = "@triggerBody()?.payload" },
            },
            ["items"] = new LoopStepMetadata
            {
                Type = "ForEach",
                RunAfter = new RunAfterCollection { ["start"] = [StepStatus.Succeeded] },
                ForEach = "@triggerBody()?.items",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["line"] = new StepMetadata { Type = "Echo", Inputs = new Dictionary<string, object?> { ["kind"] = "line ✓" } },
                },
            },
            ["gated"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection
                {
                    ["start"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "@triggerBody().amount > 1000" },
                },
                Inputs = new Dictionary<string, object?> { ["label"] = "big order" },
            },
            ["tail"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["items"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "done" },
            },
        },
    };
}

/// <summary><c>start</c> → <c>boom</c> (throws) → <c>after</c> (never runs: needs <c>boom</c> Succeeded).</summary>
public sealed class FailingFlow : IFlowDefinition
{
    public Guid Id { get; } = new("5a000000-0000-0000-0000-000000000002");
    public string Version => "2.1";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
        Steps = new StepCollection
        {
            ["start"] = new StepMetadata { Type = "Echo", Inputs = new Dictionary<string, object?> { ["label"] = "before" } },
            ["boom"] = new StepMetadata { Type = "Fail", RunAfter = new RunAfterCollection { ["start"] = [StepStatus.Succeeded] } },
            ["after"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["boom"] = [StepStatus.Succeeded] } },
        },
    };
}

/// <summary><c>hold</c> (blocks on a <see cref="Gates"/> entry) → <c>next</c>; used to cancel a run mid-flight.</summary>
public sealed class GateFlow : IFlowDefinition
{
    public Guid Id { get; } = new("5a000000-0000-0000-0000-000000000003");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
        Steps = new StepCollection
        {
            ["hold"] = new StepMetadata { Type = "Gate" },
            ["next"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["hold"] = [StepStatus.Succeeded] } },
        },
    };
}

/// <summary><c>submit</c> → <c>wait</c> (WaitForSignal <c>approval</c>) → <c>notify</c> (reads the signal payload).</summary>
public sealed class ApprovalFlow : IFlowDefinition
{
    public Guid Id { get; } = new("5a000000-0000-0000-0000-000000000004");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
        Steps = new StepCollection
        {
            ["submit"] = new StepMetadata { Type = "Echo", Inputs = new Dictionary<string, object?> { ["label"] = "submitted" } },
            ["wait"] = new StepMetadata
            {
                Type = "WaitForSignal",
                RunAfter = new RunAfterCollection { ["submit"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["signalName"] = "approval", ["timeoutSeconds"] = 86400 },
            },
            ["notify"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["wait"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["approver"] = "@steps('wait').output.approver" },
            },
        },
    };
}

/// <summary>A cron flow (once a year, never fires during a test) with a manual trigger as well.</summary>
public sealed class NightlyFlow : IFlowDefinition
{
    public Guid Id { get; } = new("5a000000-0000-0000-0000-000000000005");
    public string Version => "3.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
            ["nightly"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 0 1 1 *" },
            },
        },
        Steps = new StepCollection
        {
            ["only"] = new StepMetadata { Type = "Echo", Inputs = new Dictionary<string, object?> { ["label"] = "nightly" } },
        },
    };
}

/// <summary>Returns its resolved inputs plus a unicode note as the step output.</summary>
public sealed class EchoHandler : IStepHandler
{
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        var output = new Dictionary<string, object?>(step.Inputs) { ["note"] = "ok — Tiếng Việt ✓ 日本語 😀" };
        return ValueTask.FromResult<object?>(new StepResult { Key = step.Key, Result = output });
    }
}

/// <summary>Always throws; the engine records the step and the run as Failed.</summary>
public sealed class FailHandler : IStepHandler
{
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step) =>
        throw new InvalidOperationException("boom — échec ❌ (" + step.Key + ")");
}

/// <summary>Per-run gates: the <c>Gate</c> step signals that it entered, then waits to be released.</summary>
public sealed class Gates
{
    private readonly ConcurrentDictionary<Guid, (TaskCompletionSource Entered, TaskCompletionSource Release)> _gates = new();

    private (TaskCompletionSource Entered, TaskCompletionSource Release) For(Guid runId) =>
        _gates.GetOrAdd(runId, _ => (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                                     new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));

    /// <summary>Completes when the run's Gate step has started.</summary>
    public Task Entered(Guid runId) => For(runId).Entered.Task;

    /// <summary>Lets the run's Gate step finish.</summary>
    public void Release(Guid runId) => For(runId).Release.TrySetResult();

    internal Task EnterAndWaitAsync(Guid runId)
    {
        var gate = For(runId);
        gate.Entered.TrySetResult();
        return gate.Release.Task;
    }
}

/// <summary>Blocks until the test releases the run's gate.</summary>
public sealed class GateHandler : IStepHandler
{
    private readonly Gates _gates;

    public GateHandler(Gates gates) => _gates = gates;

    public async ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        await _gates.EnterAndWaitAsync(context.RunId).WaitAsync(TimeSpan.FromSeconds(60));
        return new StepResult { Key = step.Key, Result = new Dictionary<string, object?> { ["released"] = true } };
    }
}
