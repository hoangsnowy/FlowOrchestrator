using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace OrderHub;

/// <summary>Logs a message and returns it as the step output.</summary>
public sealed class LogMessageStep : IStepHandler<LogMessageInput>
{
    private readonly ILogger<LogMessageStep> _logger;

    public LogMessageStep(ILogger<LogMessageStep> logger) => _logger = logger;

    public ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance<LogMessageInput> step)
    {
        var text = step.Inputs.Message switch
        {
            null => step.Key,
            JsonElement { ValueKind: JsonValueKind.String } s => s.GetString() ?? step.Key,
            JsonElement e => e.GetRawText(),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString() ?? step.Key,
        };
        _logger.LogInformation("RunId={RunId} Step={StepKey} => {Message}", ctx.RunId, step.Key, text);
        return ValueTask.FromResult<object?>(new { logged = text });
    }
}

public sealed class LogMessageInput
{
    public object? Message { get; set; }
}

/// <summary>
/// Polls the "carrier": a shipment is delivered once the file <c>{StateDir}/shipment-{id}.delivered</c>
/// exists. Every fetch appends a line to <c>{StateDir}/polls-{id}.log</c>, so the rehearsal can see a
/// poll that started on one version keep polling on the other.
/// </summary>
public sealed class CarrierStatusStep : PollableStepHandler<CarrierStatusInput>
{
    private readonly string _stateDir;

    public CarrierStatusStep(IConfiguration configuration)
    {
        _stateDir = configuration["Rehearsal:StateDir"] ?? Path.Combine(AppContext.BaseDirectory, "state");
        Directory.CreateDirectory(_stateDir);
    }

    protected override ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext ctx, IFlowDefinition flow, IStepInstance<CarrierStatusInput> step)
    {
        var id = step.Inputs.ShipmentId ?? LoopItemText(step.Inputs.LoopItem)
            ?? throw new InvalidOperationException("No shipment id.");
        var delivered = File.Exists(Path.Combine(_stateDir, $"shipment-{id}.delivered"));
        File.AppendAllText(
            Path.Combine(_stateDir, $"polls-{id}.log"),
            $"{DateTimeOffset.UtcNow:O} attempt={step.Inputs.PollAttempt ?? 0} delivered={delivered} pid={Environment.ProcessId} fo={typeof(IStepHandler).Assembly.GetName().Version}{Environment.NewLine}");
        var json = JsonSerializer.SerializeToElement(new { status = delivered ? "delivered" : "in_transit", shipmentId = id });
        return ValueTask.FromResult((json, true));
    }

    private static string? LoopItemText(object? item) => item switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } s => s.GetString(),
        JsonElement e => e.GetRawText(),
        var other => other.ToString(),
    };
}

public sealed class CarrierStatusInput : IPollableInput
{
    public string? ShipmentId { get; set; }

    [JsonPropertyName("__loopItem")]
    public object? LoopItem { get; set; }

    public bool PollEnabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 5;
    public int PollTimeoutSeconds { get; set; } = 3600;
    public int PollMinAttempts { get; set; } = 1;
    public string? PollConditionPath { get; set; }
    public object? PollConditionEquals { get; set; }

    [JsonPropertyName("__pollStartedAtUtc")]
    public string? PollStartedAtUtc { get; set; }

    [JsonPropertyName("__pollAttempt")]
    public int? PollAttempt { get; set; }
}

/// <summary>Charges a card; declines (throws) for a non-positive amount.</summary>
public sealed class ChargeCardStep : IStepHandler<ChargeCardInput>
{
    public ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance<ChargeCardInput> step)
    {
        if (step.Inputs.Amount <= 0)
        {
            throw new InvalidOperationException($"Card declined for amount {step.Inputs.Amount}.");
        }
        return ValueTask.FromResult<object?>(new { charged = step.Inputs.Amount, authorization = $"AUTH-{ctx.RunId:N}"[..13] });
    }
}

public sealed class ChargeCardInput
{
    public decimal Amount { get; set; }
}

internal static class RehearsalKnobs
{
    /// <summary>Removes hosted services by implementation type name (used to run a web-only node).</summary>
    public static void RemoveHostedServices(IServiceCollection services, params string[] typeNames)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var d = services[i];
            if (d.ServiceType == typeof(IHostedService)
                && d.ImplementationType is { } t
                && Array.IndexOf(typeNames, t.Name) >= 0)
            {
                services.RemoveAt(i);
            }
        }
    }
}
