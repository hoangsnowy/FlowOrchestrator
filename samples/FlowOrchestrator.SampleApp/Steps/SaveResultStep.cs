using System.Text.Json;
using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Reads outputs from previous steps and saves a summary to the database.
/// Demonstrates accessing outputs from earlier steps in the flow.
/// Inputs: table (string)
/// </summary>
public sealed class SaveResultStep : IStepHandler
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

    public async ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        var table = step.Inputs.TryGetValue("table", out var t) ? t?.ToString() ?? "Results" : "Results";

        var fetchOutput = await _outputsRepository.GetStepOutputAsync(ctx.RunId, "fetch_orders").ConfigureAwait(false);
        var apiOutput = await _outputsRepository.GetStepOutputAsync(ctx.RunId, "enrich_data").ConfigureAwait(false);

        var summary = new
        {
            RunId = ctx.RunId,
            FetchedOrders = fetchOutput is JsonElement fe ? fe.GetRawText() : fetchOutput?.ToString(),
            ApiResponse = apiOutput is JsonElement ae ? ae.GetRawText() : apiOutput?.ToString(),
            ProcessedAt = DateTimeOffset.UtcNow
        };

        _logger.LogInformation(
            "[SaveResult] RunId={RunId} Saving to {Table}: Orders={Orders}, ApiData={ApiData}",
            ctx.RunId, table,
            summary.FetchedOrders?[..Math.Min(100, summary.FetchedOrders.Length)],
            summary.ApiResponse?[..Math.Min(100, summary.ApiResponse.Length)]);

        // Example: save to DB (table may not exist in the sample — wrapped in try/catch for demo)
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
            _logger.LogWarning(ex, "[SaveResult] Could not save to {Table} (table may not exist in sample DB)", table);
        }

        return summary;
    }
}
