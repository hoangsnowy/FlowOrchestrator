using System.Text.Json;
using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Reads resolved step outputs from its input properties and saves a combined summary to the database.
///
/// ── Using @steps() expressions ──────────────────────────────────────────────
///
/// The upstream step outputs are wired into this handler's inputs via manifest expressions:
///
///   ["fetchedOrders"] = "@steps('fetch_orders').output"
///   ["apiResult"]     = "@steps('submit_to_wms').output"
///
/// The engine resolves these before ExecuteAsync is called, so the handler receives the
/// outputs as JsonElement values and requires no IOutputsRepository injection.
///
/// Flow: OrderFulfillmentFlow
///   fetch_orders  → rows[]        (wired via fetchedOrders input)
///   submit_to_wms → WMS response  (wired via apiResult input)
///   save_result   → this step — combines both and saves to ProcessedOrders table
/// </summary>
public sealed class SaveResultStep : IStepHandler<SaveResultStepInput>
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly ILogger<SaveResultStep> _logger;

    public SaveResultStep(DbConnectionFactory dbFactory, ILogger<SaveResultStep> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance<SaveResultStepInput> step)
    {
        var table = string.IsNullOrWhiteSpace(step.Inputs.Table) ? "Results" : step.Inputs.Table;

        var summary = new SaveResultSummary
        {
            RunId         = ctx.RunId,
            FetchedOrders = ToRawJson(step.Inputs.FetchedOrders),
            ApiResponse   = ToRawJson(step.Inputs.ApiResult),
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

    private static string? ToRawJson(object? value) =>
        value switch
        {
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement el => el.GetRawText(),
            string s when !string.IsNullOrWhiteSpace(s) => s,
            _ => null
        };
}

public sealed class SaveResultStepInput
{
    /// <summary>Target table name for the INSERT statement.</summary>
    public string? Table { get; set; }

    /// <summary>
    /// Resolved output of the fetch step, supplied via <c>@steps('fetch_orders').output</c>
    /// in the flow manifest.
    /// </summary>
    public object? FetchedOrders { get; set; }

    /// <summary>
    /// Resolved output of the WMS API step, supplied via <c>@steps('submit_to_wms').output</c>
    /// in the flow manifest.
    /// </summary>
    public object? ApiResult { get; set; }
}

public sealed class SaveResultSummary
{
    public Guid RunId { get; set; }
    public string? FetchedOrders { get; set; }
    public string? ApiResponse { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
