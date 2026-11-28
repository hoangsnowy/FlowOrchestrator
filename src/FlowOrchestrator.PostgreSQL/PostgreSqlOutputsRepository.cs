using System.Text.Json;
using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// Dapper-based PostgreSQL implementation of <see cref="IOutputsRepository"/>.
/// Persists step inputs/outputs and trigger data to the <c>flow_outputs</c> table.
/// </summary>
internal sealed class PostgreSqlOutputsRepository : IOutputsRepository
{
    private static readonly JsonSerializerOptions _webOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;

    public PostgreSqlOutputsRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async ValueTask SaveStepOutputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result)
    {
        var json = SerializeToJson(result.Result);
        await UpsertAsync(ctx.RunId, step.Key, json).ConfigureAwait(false);
    }

    public async ValueTask SaveStepInputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        var json = SerializeToJson(step.Inputs);
        await UpsertAsync(ctx.RunId, $"{step.Key}:input", json).ConfigureAwait(false);
    }

    public async ValueTask SaveTriggerDataAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger)
    {
        var json = SerializeToJson(trigger.Data);
        await UpsertAsync(ctx.RunId, "__trigger:data", json).ConfigureAwait(false);
    }

    public async ValueTask SaveTriggerHeadersAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger)
    {
        if (trigger.Headers is null) return;
        var json = SerializeToJson(trigger.Headers);
        await UpsertAsync(ctx.RunId, "__trigger:headers", json).ConfigureAwait(false);
    }

    public async ValueTask<object?> GetTriggerDataAsync(Guid runId)
    {
        return await GetJsonElementAsync(runId, "__trigger:data").ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyDictionary<string, string>?> GetTriggerHeadersAsync(Guid runId)
    {
        var element = await GetJsonElementAsync(runId, "__trigger:headers").ConfigureAwait(false);
        if (element is null) return null;

        if (element is JsonElement je && je.ValueKind == JsonValueKind.Object)
            return ToHeaderDictionary(je);

        return null;
    }

    /// <summary>
    /// Rehydrates stored trigger headers into a case-insensitive dictionary.
    /// </summary>
    /// <remarks>
    /// HTTP header names are case-insensitive, and the entry step resolves <c>@triggerHeaders()['name']</c>
    /// against the trigger's own case-insensitive dictionary. Later steps read the headers back from
    /// here, so a case-sensitive copy made <c>@triggerHeaders()['x-request-id']</c> resolve in the entry
    /// step and return <see langword="null"/> afterwards. Indexer assignment (not <c>Add</c>) keeps a
    /// payload carrying two casings of one name from throwing: the last one wins.
    /// </remarks>
    private static Dictionary<string, string> ToHeaderDictionary(JsonElement headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers.EnumerateObject())
        {
            switch (header.Value.ValueKind)
            {
                case JsonValueKind.String:
                    result[header.Name] = header.Value.GetString()!;
                    break;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    break;
                default:
                    result[header.Name] = header.Value.GetRawText();
                    break;
            }
        }

        return result;
    }

    public async ValueTask<object?> GetStepOutputAsync(Guid runId, string stepKey)
    {
        return await GetJsonElementAsync(runId, stepKey).ConfigureAwait(false);
    }

    private async Task UpsertAsync(Guid runId, string key, string? valueJson)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO flow_outputs (run_id, key, value_json, created_at)
            VALUES (@RunId, @Key, @ValueJson, NOW())
            ON CONFLICT (run_id, key) DO UPDATE SET
                value_json = EXCLUDED.value_json
            """,
            new { RunId = runId, Key = key, ValueJson = valueJson }).ConfigureAwait(false);
    }

    private async Task<object?> GetJsonElementAsync(Guid runId, string key)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var json = await conn.ExecuteScalarAsync<string?>(
            "SELECT value_json FROM flow_outputs WHERE run_id = @RunId AND key = @Key",
            new { RunId = runId, Key = key }).ConfigureAwait(false);

        if (json is null) return null;

        return JsonSerializer.Deserialize<JsonElement>(json, _webOptions);
    }

    private static string? SerializeToJson(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement je)
            return je.ValueKind == JsonValueKind.Undefined ? null : je.GetRawText();
        return JsonSerializer.Serialize(value, value.GetType(), _webOptions);
    }
}
