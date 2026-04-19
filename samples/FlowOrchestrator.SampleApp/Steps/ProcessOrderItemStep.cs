using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Validates and logs a single order item within a ForEach loop iteration.
///
/// ── Advanced topic: ForEach child step handlers ────────────────────────────
///
/// When a step runs inside a <see cref="LoopStepMetadata"/> scope, FlowOrchestrator
/// injects two extra keys into the step's input dictionary at execution time:
///
///   __loopItem  — the current element from the ForEach source collection.
///                 Type is object? (may be a JsonElement, string, or number depending
///                 on how the source was resolved from @triggerBody()).
///
///   __loopIndex — the zero-based iteration index (int).
///
/// These keys are injected by <c>ForEachStepHandler</c> AFTER the manifest inputs
/// are copied, so they are available alongside any static inputs declared in the
/// flow manifest.
///
/// To receive them in a typed handler:
///   1. Add properties to your input class and annotate with <see cref="JsonPropertyNameAttribute"/>
///      using the double-underscore names ("__loopItem", "__loopIndex").
///   2. FlowOrchestrator's input deserialiser will populate them automatically.
///
/// Runtime step key format:
///   {parentForEachKey}.{zeroBasedIndex}.{childStepKey}
///   e.g.  "process_orders.0.validate_order"
///           "process_orders.1.validate_order"
///
/// The parent step key and index are embedded in <see cref="IStepInstance.Key"/>,
/// so you can use <c>step.Index</c> as a shortcut without parsing the key string.
///
/// Used by: OrderBatchFlow → process_orders scope → validate_order step.
/// </summary>
public sealed class ProcessOrderItemStep : IStepHandler<ProcessOrderItemInput>
{
    private readonly ILogger<ProcessOrderItemStep> _logger;

    /// <summary>Initialises the step handler with the application logger.</summary>
    public ProcessOrderItemStep(ILogger<ProcessOrderItemStep> logger) => _logger = logger;

    /// <inheritdoc/>
    public ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance<ProcessOrderItemInput> step)
    {
        var input = step.Inputs;

        // __loopItem arrives as object? — JsonElement when resolved from @triggerBody(),
        // or a plain string/number when the ForEach source is a static array in the manifest.
        var orderId = input.OrderId switch
        {
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            JsonElement el                                            => el.GetRawText(),
            string s                                                  => s,
            null                                                      => "(null)",
            var other                                                  => other.ToString()
        };

        _logger.LogInformation(
            "[ProcessOrderItem] RunId={RunId} Step={StepKey} Index={Index} OrderId={OrderId}",
            ctx.RunId, step.Key, step.Index, orderId);

        // Return a typed result so downstream steps can read it via
        // IOutputsRepository.GetStepOutputAsync<ProcessOrderItemOutput>(runId, stepKey).
        var result = new ProcessOrderItemOutput
        {
            OrderId   = orderId,
            Index     = step.Index,
            Validated = true,
            Note      = $"Order {orderId} validated at index {step.Index}."
        };

        return ValueTask.FromResult<object?>(new StepResult<ProcessOrderItemOutput>
        {
            Key   = step.Key,
            Value = result
        });
    }
}

/// <summary>
/// Input contract for <see cref="ProcessOrderItemStep"/>.
/// </summary>
/// <remarks>
/// <c>OrderId</c> and <c>Index</c> are populated by the <c>ForEachStepHandler</c>
/// at execution time via the reserved keys <c>__loopItem</c> and <c>__loopIndex</c>.
/// Any additional manifest inputs (e.g. <c>MaxOrderValue</c>) are merged in before injection.
/// </remarks>
public sealed class ProcessOrderItemInput
{
    /// <summary>
    /// The current loop element, injected by ForEachStepHandler as <c>__loopItem</c>.
    /// May be a <see cref="JsonElement"/>, string, number, or <see langword="null"/>
    /// depending on the ForEach source expression.
    /// </summary>
    [JsonPropertyName("__loopItem")]
    public object? OrderId { get; set; }

    /// <summary>
    /// Zero-based iteration index, injected by ForEachStepHandler as <c>__loopIndex</c>.
    /// Mirrors <see cref="IStepInstance.Index"/>.
    /// </summary>
    [JsonPropertyName("__loopIndex")]
    public int Index { get; set; }

    /// <summary>
    /// Optional static input from the manifest — maximum order value to accept.
    /// Defaults to 0 (no limit) when not specified.
    /// </summary>
    public decimal MaxOrderValue { get; set; }
}

/// <summary>Output produced by <see cref="ProcessOrderItemStep"/> per iteration.</summary>
public sealed class ProcessOrderItemOutput
{
    /// <summary>The order identifier that was validated.</summary>
    public string? OrderId { get; set; }

    /// <summary>Zero-based index of this item within the ForEach collection.</summary>
    public int Index { get; set; }

    /// <summary><see langword="true"/> when the order passed validation rules.</summary>
    public bool Validated { get; set; }

    /// <summary>Human-readable outcome note.</summary>
    public string? Note { get; set; }
}
