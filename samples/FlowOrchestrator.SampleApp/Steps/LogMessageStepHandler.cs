using System.Globalization;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

public sealed class LogMessageStepHandler : IStepHandler<LogMessageStepInput>
{
    private readonly ILogger<LogMessageStepHandler> _logger;

    public LogMessageStepHandler(ILogger<LogMessageStepHandler> logger) => _logger = logger;

    public ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance<LogMessageStepInput> step)
    {
        var msg = ToMessageText(step.Inputs.Message, step.Key);
        _logger.LogInformation("[FlowOrchestrator] RunId={RunId} Step={StepKey} => {Message}", ctx.RunId, step.Key, msg);
        return ValueTask.FromResult<object?>(new StepResult<LogMessageStepOutput>
        {
            Key = step.Key,
            Value = new LogMessageStepOutput { Logged = msg }
        });
    }

    private static string ToMessageText(object? message, string fallback) => message switch
    {
        null => fallback,
        string s when !string.IsNullOrWhiteSpace(s) => s,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => fallback,
        JsonElement { ValueKind: JsonValueKind.String } element
            when !string.IsNullOrWhiteSpace(element.GetString()) => element.GetString()!,
        JsonElement element => element.GetRawText(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => string.IsNullOrWhiteSpace(message.ToString()) ? fallback : message.ToString()!
    };
}

public sealed class LogMessageStepInput
{
    public object? Message { get; set; }
}

public sealed class LogMessageStepOutput
{
    public string? Logged { get; set; }
}
