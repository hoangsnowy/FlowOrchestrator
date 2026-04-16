using System.Text.Json;
using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Reads outputs from two upstream steps and saves a combined summary to the database.
///
/// ── Advanced topic: Reading upstream step outputs via IOutputsRepository ────
///
/// Every step's return value is persisted to FlowOutputs keyed by (RunId, stepKey).
/// Use IOutputsRepository.GetStepOutputAsync{T}(runId, stepKey) in any downstream
/// step to retrieve those outputs — without needing to pass data through inputs.
///
/// Key points:
///   • IOutputsRepository is injected via the constructor like any other DI service.
///   • ctx.RunId scopes the lookup to the current flow run.
///   • The step keys (e.g. "fetch_orders", "submit_to_wms") are made configurable
///     via SaveResultStepInput so this handler can be reused across different flows
///     without hardcoding predecessor step names.
///   • If a referenced step did not run (e.g. was skipped), GetStepOutputAsync
///     returns default(T) — always check ValueKind before using the result.
///
/// Flow: OrderFulfillmentFlow
///   fetch_orders  → rows[]        (read by ordersStepKey input)
///   submit_to_wms → WMS response  (read by apiResultStepKey input)
///   save_result   → this step — combines both and saves to ProcessedOrders table
/// </summary>
public sealed class SaveResultStep : IStepHandler<SaveResultStepInput>
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly IOutputsRepository _outputsRepository;
    private readonly ILogger<SaveResultStep> _logger;

    public SaveResultStep(
        DbConnectionFactory dbFactory,
        IOutputsRepository outputsRepository,
        ILogger<SaveResultStep> logger)
    {
        _dbFactory = dbFactory;
        _outputsRepository = outputsRepository;
        _logger = logger;
    }

    public async ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance<SaveResultStepInput> step)
    {
        var table          = string.IsNullOrWhiteSpace(step.Inputs.Table) ? "Results" : step.Inputs.Table;
        var ordersStepKey  = string.IsNullOrWhiteSpace(step.Inputs.OrdersStepKey)    ? "fetch_orders"   : step.Inputs.OrdersStepKey;
        var apiStepKey     = string.IsNullOrWhiteSpace(step.Inputs.ApiResultStepKey) ? "submit_to_wms"  : step.Inputs.ApiResultStepKey;

        // Retrieve outputs produced by the two upstream steps in this run.
        // The step keys are supplied via manifest Inputs so this handler stays reusable.
        var fetchOutput = await _outputsRepository.GetStepOutputAsync<JsonElement>(ctx.RunId, ordersStepKey).ConfigureAwait(false);
        var apiOutput   = await _outputsRepository.GetStepOutputAsync<JsonElement>(ctx.RunId, apiStepKey).ConfigureAwait(false);

        var summary = new SaveResultSummary
        {
            RunId         = ctx.RunId,
            FetchedOrders = ToRawJson(fetchOutput),
            ApiResponse   = ToRawJson(apiOutput),
            ProcessedAt   = DateTimeOffset.UtcNow
        };

        _logger.LogInformation(
            "[SaveResult] RunId={RunId} Table={Table} Orders={Orders} Api={Api}",
            ctx.RunId, table,
            summary.FetchedOrders?[..Math.Min(80, summary.FetchedOrders.Length)],
            summary.ApiResponse?[..Math.Min(80, summary.ApiResponse.Length)]);

        // Persist to the database. The target table may not exist in the sample schema —
        // wrapped in try/catch so the demo still shows a Succeeded step with useful output.
        try
        {
            using var connection = _dbFactory.Create();
            await connection.ExecuteAsync(
                $"INSERT INTO {table} (RunId, Data, CreatedAt) VALUES (@RunId, @Data, @CreatedAt)",
                new { ctx.RunId, Data = JsonSerializer.Serialize(summary), CreatedAt = DateTimeOffset.UtcNow })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SaveResult] Could not write to {Table} (table may not exist in sample DB — this is expected)", table);
        }

        return new StepResult<SaveResultSummary> { Key = step.Key, Value = summary };
    }

    private static string? ToRawJson(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.GetRawText();
}

public sealed class SaveResultStepInput
{
    /// <summary>Target table name for the INSERT statement.</summary>
    public string? Table { get; set; }

    /// <summary>
    /// Step key whose output holds the fetched order rows.
    /// Defaults to "fetch_orders" — override in the manifest Inputs to reuse this
    /// handler in other flows that use a different predecessor step name.
    /// </summary>
    public string? OrdersStepKey { get; set; }

    /// <summary>
    /// Step key whose output holds the API/WMS response.
    /// Defaults to "submit_to_wms".
    /// </summary>
    public string? ApiResultStepKey { get; set; }
}

public sealed class SaveResultSummary
{
    public Guid RunId { get; set; }
    public string? FetchedOrders { get; set; }
    public string? ApiResponse { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
