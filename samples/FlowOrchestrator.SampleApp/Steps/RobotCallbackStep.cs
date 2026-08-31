using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Final step of <c>WarehouseRobotFlow</c>: notifies the robot controller that the whole scan
/// job finished. Reads the ForEach step's own output (<c>@steps('scan_process').output.iterations</c>)
/// for the summary line.
/// </summary>
/// <remarks>
/// This step declares <c>RunAfter = { scan_process: [Succeeded] }</c>, so the loop completion
/// barrier (v1.30.1, issue #169) guarantees it executes only after every location was scanned.
/// The log line is the cheapest possible ordering probe: if it ever appears before the last
/// <c>open_camera</c> capture line, the barrier is broken.
/// </remarks>
public sealed class RobotCallbackStep : IStepHandler<RobotCallbackInput>
{
    private readonly ILogger<RobotCallbackStep> _logger;

    /// <summary>Initialises the step handler with the application logger.</summary>
    public RobotCallbackStep(ILogger<RobotCallbackStep> logger) => _logger = logger;

    /// <inheritdoc/>
    public ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance<RobotCallbackInput> step)
    {
        var orderNo = AsText(step.Inputs.OrderNo) ?? "(no order)";
        var scanned = AsInt(step.Inputs.ScannedCount);

        _logger.LogInformation(
            "[WarehouseRobot] RunId={RunId} Step={StepKey} => scan job {OrderNo} complete: {ScannedCount} location(s) scanned; robot released",
            ctx.RunId, step.Key, orderNo, scanned);

        return ValueTask.FromResult<object?>(new StepResult<RobotCallbackOutput>
        {
            Key = step.Key,
            Value = new RobotCallbackOutput
            {
                OrderNo = orderNo,
                ScannedCount = scanned,
                NotifiedAt = DateTimeOffset.UtcNow
            }
        });
    }

    private static string? AsText(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement el => el.GetRawText(),
        _ => value.ToString()
    };

    private static int AsInt(object? value) => value switch
    {
        int i => i,
        JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetInt32(out var n) => n,
        _ => 0
    };
}

/// <summary>Typed inputs for the <c>RobotCallback</c> step.</summary>
public sealed class RobotCallbackInput
{
    /// <summary>Warehouse order the scan job belongs to — resolved from the trigger body.</summary>
    public object? OrderNo { get; set; }

    /// <summary>Iteration count read from the ForEach step's own output.</summary>
    public object? ScannedCount { get; set; }
}

/// <summary>Callback acknowledgement persisted as the step output.</summary>
public sealed class RobotCallbackOutput
{
    /// <summary>Order number echoed back for correlation.</summary>
    public string? OrderNo { get; set; }

    /// <summary>Number of locations the job scanned.</summary>
    public int ScannedCount { get; set; }

    /// <summary>UTC time the robot controller was notified.</summary>
    public DateTimeOffset NotifiedAt { get; set; }
}
