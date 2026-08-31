using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Simulates a camera capture at a warehouse location: a short exposure delay, then a
/// scanned barcode for the location the robot reached.
/// </summary>
/// <remarks>
/// Runs inside <c>WarehouseRobotFlow</c>'s ForEach scope. Its <c>Location</c> input is the
/// scope-relative expression <c>@steps('wait_robot_goto').output.Location</c>, so each
/// iteration photographs the location <b>its own</b> robot signal reported — the log line
/// makes a wrong-scope resolve (every iteration reading iteration 0) immediately visible.
/// </remarks>
public sealed class OpenCameraStep : IStepHandler<OpenCameraInput>
{
    private readonly ILogger<OpenCameraStep> _logger;

    /// <summary>Initialises the step handler with the application logger.</summary>
    public OpenCameraStep(ILogger<OpenCameraStep> logger) => _logger = logger;

    /// <inheritdoc/>
    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance<OpenCameraInput> step)
    {
        var location = AsText(step.Inputs.Location) ?? "(unknown)";
        var orderNo = AsText(step.Inputs.OrderNo) ?? "(no order)";

        // Simulated exposure time — long enough to be visible on the run timeline,
        // short enough not to slow the demo down.
        await Task.Delay(TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(250, 700)));

        var barcode = $"SKU-{RandomNumberGenerator.GetInt32(100_000, 999_999).ToString(CultureInfo.InvariantCulture)}";
        _logger.LogInformation(
            "[WarehouseRobot] RunId={RunId} Step={StepKey} => camera captured {Barcode} at location {Location} (order {OrderNo})",
            ctx.RunId, step.Key, barcode, location, orderNo);

        return new StepResult<OpenCameraOutput>
        {
            Key = step.Key,
            Value = new OpenCameraOutput
            {
                Location = location,
                Barcode = barcode,
                CapturedAt = DateTimeOffset.UtcNow
            }
        };
    }

    // Inputs arrive as object? because @steps()/@triggerBody() expressions resolve to
    // JsonElement at runtime; static manifest values arrive as plain strings.
    private static string? AsText(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement el => el.GetRawText(),
        _ => value.ToString()
    };
}

/// <summary>Typed inputs for the <c>OpenCamera</c> step.</summary>
public sealed class OpenCameraInput
{
    /// <summary>Location the robot reported reaching — resolved from the sibling waiter's output.</summary>
    public object? Location { get; set; }

    /// <summary>Warehouse order the scan job belongs to — resolved from the trigger body.</summary>
    public object? OrderNo { get; set; }
}

/// <summary>Camera capture result persisted as the step output.</summary>
public sealed class OpenCameraOutput
{
    /// <summary>Location that was photographed.</summary>
    public string? Location { get; set; }

    /// <summary>Barcode decoded from the captured frame.</summary>
    public string? Barcode { get; set; }

    /// <summary>UTC capture timestamp.</summary>
    public DateTimeOffset CapturedAt { get; set; }
}
