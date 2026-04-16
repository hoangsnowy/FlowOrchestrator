using System.Text.Json;
using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Executes a parameterised SQL query and returns the result rows as a list of
/// string→object dictionaries.
///
/// ── Advanced topic: DI service injection + typed inputs + Dapper ────────────
///
/// This step demonstrates three important patterns:
///
///   1. Constructor injection — DbConnectionFactory and ILogger are resolved from
///      the DI container automatically. Register your services alongside the handler:
///
///        builder.Services.AddSingleton(new DbConnectionFactory(connectionString));
///        builder.Services.AddStepHandler<QueryDatabaseStep>("QueryDatabase");
///
///   2. Typed + nullable inputs — QueryDatabaseStepInput maps the manifest's Inputs
///      dictionary. Fields are optional at the C# level; validation happens inside
///      ExecuteAsync (throw ArgumentException for required fields).
///
///   3. JsonElement unboxing — when step inputs arrive from an expression like
///      @triggerBody()?.params, the value is a JsonElement. The UnboxJsonElement
///      helper converts it to the right .NET type before handing it to Dapper.
///
/// Used by: OrderFulfillmentFlow → fetch_orders step
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
            throw new ArgumentException("Input 'sql' is required for QueryDatabase step.");

        // Build Dapper parameters — values may arrive as JsonElement if the
        // step inputs were populated from a @triggerBody() expression.
        var parameters = new DynamicParameters();
        if (step.Inputs.Parameters is not null)
        {
            foreach (var kvp in step.Inputs.Parameters)
                parameters.Add(kvp.Key, UnboxJsonElement(kvp.Value));
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

    // Dapper cannot handle JsonElement — unwrap to a .NET primitive.
    private static object? UnboxJsonElement(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String }  je => je.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number }  je => je.GetDecimal(),
        JsonElement { ValueKind: JsonValueKind.True }       => true,
        JsonElement { ValueKind: JsonValueKind.False }      => false,
        JsonElement { ValueKind: JsonValueKind.Null
                   or JsonValueKind.Undefined }             => null,
        _                                                   => value
    };
}

public sealed class QueryDatabaseStepInput
{
    /// <summary>Parameterised SQL to execute (e.g. "SELECT * FROM Orders WHERE Status = @Status").</summary>
    public string? Sql { get; set; }

    /// <summary>Optional named parameters. Values can be plain types or JsonElement.</summary>
    public Dictionary<string, object?>? Parameters { get; set; }
}
