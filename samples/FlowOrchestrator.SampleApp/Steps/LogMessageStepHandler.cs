using System.Globalization;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Logs a message to the application log and returns the logged text as output.
///
/// ── Advanced topic: Minimal typed step handler ─────────────────────────────
///
/// This is the simplest possible IStepHandler{T} implementation. It shows the
/// essential shape every step handler must follow:
///
///   1. Declare a typed input class (LogMessageStepInput) whose properties map
///      to the keys in the flow manifest's Inputs dictionary. FlowOrchestrator
///      deserializes the resolved inputs into this class before calling ExecuteAsync.
///
///   2. Return any object as the step output. The output is serialized to JSON and
///      stored in FlowOutputs, making it available to downstream steps via
///      IOutputsRepository.GetStepOutputAsync(runId, stepKey).
///
///   3. The Message input accepts object? (not string) because FlowOrchestrator
///      resolves @triggerBody() expressions to JsonElement at runtime. The
///      ToMessageText helper normalises all variants into a plain string.
///
/// See SaveResultStep for how downstream steps read this step's output.
/// </summary>
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

    // Message arrives as object? because @triggerBody() resolves to JsonElement.
    // This helper normalises all runtime variants into a plain string.
    private static string ToMessageText(object? message, string fallback) => message switch
    {
        null                                                                                   => fallback,
        string s when !string.IsNullOrWhiteSpace(s)                                           => s,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }               => fallback,
        JsonElement { ValueKind: JsonValueKind.String } el
            when !string.IsNullOrWhiteSpace(el.GetString())                                    => el.GetString()!,
        JsonElement el                                                                          => el.GetRawText(),
        IFormattable f                                                                          => f.ToString(null, CultureInfo.InvariantCulture),
        _                                                                                       => string.IsNullOrWhiteSpace(message.ToString()) ? fallback : message.ToString()!
    };
}

public sealed class LogMessageStepInput
{
    // object? so the runtime can pass either a plain string or a JsonElement
    // resolved from a @triggerBody() expression.
    public object? Message { get; set; }
}

public sealed class LogMessageStepOutput
{
    public string? Logged { get; set; }
}
