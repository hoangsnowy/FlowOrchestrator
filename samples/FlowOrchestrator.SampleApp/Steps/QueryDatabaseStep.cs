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
public sealed class QueryDatabaseStep : IStepHandler<QueryDatabaseStepInput>
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly ILogger<QueryDatabaseStep> _logger;

    public QueryDatabaseStep(DbConnectionFactory dbFactory, ILogger<QueryDatabaseStep> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance<QueryDatabaseStepInput> step)
    {
        var sql = step.Inputs.Sql;
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("Input 'sql' is required for QueryDatabase step.");
        }

        var parameters = new DynamicParameters();
        if (step.Inputs.Parameters is not null)
        {
            foreach (var kvp in step.Inputs.Parameters)
            {
                parameters.Add(kvp.Key, UnboxJsonElement(kvp.Value));
            }
        }

        _logger.LogInformation("[QueryDatabase] RunId={RunId} Executing: {Sql}", ctx.RunId, sql);

        using var connection = _dbFactory.Create();
        var rows = (await connection.QueryAsync(sql, parameters).ConfigureAwait(false))
            .Select(row => row as IDictionary<string, object?> ?? new Dictionary<string, object?>())
            .Select(row => row.ToDictionary(kvp => kvp.Key, kvp => UnboxJsonElement(kvp.Value)))
            .ToList();

        _logger.LogInformation("[QueryDatabase] RunId={RunId} Returned {Count} rows", ctx.RunId, rows.Count);

        return new StepResult<IReadOnlyList<Dictionary<string, object?>>>
        {
            Key = step.Key,
            Value = rows
        };
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

public sealed class QueryDatabaseStepInput
{
    public string? Sql { get; set; }
    public Dictionary<string, object?>? Parameters { get; set; }
}
