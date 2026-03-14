using System.Text.Json;
using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Executes a SQL query and returns the result rows.
/// Inputs: sql (string), parameters (optional dictionary)
/// Output: list of rows as dictionaries
/// </summary>
public sealed class QueryDatabaseStep : IStepHandler
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly ILogger<QueryDatabaseStep> _logger;

    public QueryDatabaseStep(DbConnectionFactory dbFactory, ILogger<QueryDatabaseStep> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        var sql = step.Inputs.TryGetValue("sql", out var s) ? s?.ToString() : null;
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("Input 'sql' is required for QueryDatabase step.");

        var parameters = new DynamicParameters();
        if (step.Inputs.TryGetValue("parameters", out var p) && p is not null)
        {
            var dict = p switch
            {
                JsonElement je => JsonSerializer.Deserialize<Dictionary<string, object?>>(je.GetRawText()),
                IDictionary<string, object?> d => d,
                _ => null
            };

            if (dict is not null)
            {
                foreach (var kvp in dict)
                    parameters.Add(kvp.Key, UnboxJsonElement(kvp.Value));
            }
        }

        _logger.LogInformation("[QueryDatabase] RunId={RunId} Executing: {Sql}", ctx.RunId, sql);

        using var connection = _dbFactory.Create();
        var rows = (await connection.QueryAsync(sql, parameters).ConfigureAwait(false)).AsList();

        _logger.LogInformation("[QueryDatabase] RunId={RunId} Returned {Count} rows", ctx.RunId, rows.Count);

        return rows;
    }

    private static object? UnboxJsonElement(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDecimal(),
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        _ => value
    };
}
