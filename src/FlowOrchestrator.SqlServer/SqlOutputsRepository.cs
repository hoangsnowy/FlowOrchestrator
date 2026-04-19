using System.Text.Json;
using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Dapper-based SQL Server implementation of <see cref="IOutputsRepository"/> and <see cref="IFlowEventReader"/>.
/// Persists step inputs/outputs and trigger data to the <c>FlowOutputs</c> table,
/// and events to the <c>FlowEvents</c> table.
/// </summary>
internal sealed class SqlOutputsRepository : IOutputsRepository
    , IFlowEventReader
{
    private static readonly JsonSerializerOptions _webOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;

    public SqlOutputsRepository(string connectionString)
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
            return je.Deserialize<Dictionary<string, string>>(_webOptions);

        return null;
    }

    public async ValueTask<object?> GetStepOutputAsync(Guid runId, string stepKey)
    {
        return await GetJsonElementAsync(runId, stepKey).ConfigureAwait(false);
    }

    public ValueTask EndScopeAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
        => ValueTask.CompletedTask;

    public async ValueTask RecordEventAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, FlowEvent evt)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO FlowEvents (RunId, Timestamp, Type, StepKey, Message)
            VALUES (@RunId, @Timestamp, @Type, @StepKey, @Message)
            """,
            new
            {
                RunId = ctx.RunId,
                Timestamp = evt.Timestamp,
                evt.Type,
                StepKey = evt.StepKey ?? step.Key,
                evt.Message
            }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FlowEventRecord>> GetRunEventsAsync(Guid runId, int skip = 0, int take = 200)
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowEventRecord>(
            """
            SELECT
                Sequence,
                RunId,
                Timestamp,
                Type,
                StepKey,
                Message
            FROM FlowEvents
            WHERE RunId = @RunId
            ORDER BY Sequence
            OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
            """,
            new
            {
                RunId = runId,
                Skip = Math.Max(0, skip),
                Take = Math.Max(1, take)
            }).ConfigureAwait(false);

        return rows.AsList();
    }

    private async Task UpsertAsync(Guid runId, string key, string? valueJson)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            MERGE FlowOutputs AS target
            USING (SELECT @RunId AS RunId, @Key AS [Key]) AS source
            ON target.RunId = source.RunId AND target.[Key] = source.[Key]
            WHEN MATCHED THEN
                UPDATE SET ValueJson = @ValueJson
            WHEN NOT MATCHED THEN
                INSERT (RunId, [Key], ValueJson)
                VALUES (@RunId, @Key, @ValueJson);
            """, new { RunId = runId, Key = key, ValueJson = valueJson }).ConfigureAwait(false);
    }

    private async Task<object?> GetJsonElementAsync(Guid runId, string key)
    {
        await using var conn = new SqlConnection(_connectionString);
        var json = await conn.ExecuteScalarAsync<string?>(
            "SELECT ValueJson FROM FlowOutputs WHERE RunId = @RunId AND [Key] = @Key",
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
