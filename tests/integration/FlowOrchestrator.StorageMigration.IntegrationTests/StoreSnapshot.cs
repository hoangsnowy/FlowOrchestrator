using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.PostgreSQL;
using FlowOrchestrator.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.StorageMigration.IntegrationTests;

/// <summary>
/// The public store APIs of one database, resolved from a real <c>AddFlowOrchestrator</c> container whose
/// hosted services are never started (so reading changes nothing — not even a flow definition's UpdatedAt).
/// </summary>
public sealed class StoreReader : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private StoreReader(ServiceProvider services) => _services = services;

    public static StoreReader Create(FlowStorageProvider provider, string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestrator(o =>
        {
            if (provider == FlowStorageProvider.SqlServer)
            {
                o.UseSqlServer(connectionString);
                o.AddSqlServerWebhookHardening(connectionString);
            }
            else
            {
                o.UsePostgreSql(connectionString);
                o.AddPostgreSqlWebhookHardening(connectionString);
            }

            o.UseInMemoryRuntime();
        });
        return new StoreReader(services.BuildServiceProvider());
    }

    public IFlowStore Flows => _services.GetRequiredService<IFlowStore>();
    public IFlowRunStore Runs => _services.GetRequiredService<IFlowRunStore>();
    public IFlowRunRuntimeStore Runtime => _services.GetRequiredService<IFlowRunRuntimeStore>();
    public IFlowRunControlStore Controls => _services.GetRequiredService<IFlowRunControlStore>();
    public IOutputsRepository Outputs => _services.GetRequiredService<IOutputsRepository>();
    public IFlowSignalStore Signals => _services.GetRequiredService<IFlowSignalStore>();
    public IFlowScheduleStateStore Schedules => _services.GetRequiredService<IFlowScheduleStateStore>();
    public IWebhookRejectionStore Rejections => _services.GetRequiredService<IWebhookRejectionStore>();
    public IWebhookReplayStore Replay => _services.GetRequiredService<IWebhookReplayStore>();

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

/// <summary>
/// Everything a FlowOrchestrator host can read back through the store APIs, as canonical JSON per fact.
/// Timestamps are compared as UTC instants at microsecond precision (what both backends keep); collections are
/// sorted by key so backend-specific ordering of ties does not count as a difference.
/// </summary>
public static class StoreSnapshot
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new MicrosecondUtcConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Canonical(object? value) => JsonSerializer.Serialize(value, Json);

    public static async Task<SortedDictionary<string, string>> CaptureAsync(StoreReader store, SeedInfo seed)
    {
        var facts = new SortedDictionary<string, string>(StringComparer.Ordinal);

        var flows = (await store.Flows.GetAllAsync()).OrderBy(f => f.Id).ToList();
        facts["flows"] = Canonical(flows);

        var runs = (await store.Runs.GetRunsAsync(null, 0, 10_000)).OrderBy(r => r.Id).ToList();
        facts["runs.list"] = Canonical(runs.Select(r => new { r.Id, r.FlowId, r.FlowName, r.Status, r.TriggerKey, r.BackgroundJobId, r.StartedAt, r.CompletedAt, r.SourceRunId }));
        foreach (var listed in runs)
        {
            var run = (await store.Runs.GetRunDetailAsync(listed.Id))!;
            if (run.TriggerHeaders is { } headers)
                run.TriggerHeaders = new SortedDictionary<string, string>(headers.ToDictionary(), StringComparer.Ordinal);
            var steps = (run.Steps ?? []).OrderBy(s => s.StepKey, StringComparer.Ordinal).ToList();
            foreach (var step in steps)
                step.Attempts = step.Attempts?.OrderBy(a => a.Attempt).ToList();
            run.Steps = steps;
            facts[$"run.{run.Id}"] = Canonical(run);

            facts[$"run.{run.Id}.trigger"] = Canonical(await store.Outputs.GetTriggerDataAsync(run.Id));
            var triggerHeaders = await store.Outputs.GetTriggerHeadersAsync(run.Id);
            facts[$"run.{run.Id}.headers"] = Canonical(triggerHeaders?.OrderBy(h => h.Key, StringComparer.Ordinal).ToList());
            foreach (var step in steps)
                facts[$"run.{run.Id}.output.{step.StepKey}"] = Canonical(await store.Outputs.GetStepOutputAsync(run.Id, step.StepKey));

            var control = await store.Controls.GetRunControlAsync(run.Id);
            facts[$"run.{run.Id}.control"] = Canonical(control is null ? null : new
            {
                control.RunId, control.FlowId, control.TriggerKey, control.IdempotencyKey, control.TimeoutAtUtc,
                control.CancelRequested, control.CancelReason, control.CancelRequestedAtUtc, control.TimedOutAtUtc,
            });
            facts[$"run.{run.Id}.dispatched"] = Canonical((await store.Runs.GetDispatchedStepKeysAsync(run.Id)).Order(StringComparer.Ordinal));
            var statuses = await store.Runtime.GetStepStatusesAsync(run.Id);
            facts[$"run.{run.Id}.statuses"] = Canonical(statuses.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}"));
        }

        foreach (var (flowId, key) in seed.IdempotencyKeys)
            facts[$"idempotency.{flowId}.{key}"] = Canonical(await store.Controls.FindRunIdByIdempotencyKeyAsync(flowId, "manual", key));

        facts["schedules"] = Canonical((await store.Schedules.GetAllAsync()).OrderBy(s => s.JobId, StringComparer.Ordinal));
        facts["signal.waiter"] = Canonical(await store.Signals.GetWaiterAsync(seed.ApprovalRunId, "wait"));
        facts["webhooks"] = Canonical((await store.Rejections.QueryRecentAsync(null, null, true, 0, 1_000)).OrderBy(r => r.Id));
        facts["stats.page"] = Canonical((await store.Runs.GetRunsPageAsync(null, null, 0, 1_000)).TotalCount);
        return facts;
    }

    /// <summary>Writes a timestamp as its UTC instant truncated to the microsecond.</summary>
    private sealed class MicrosecondUtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            var ticks = value.UtcTicks;
            writer.WriteNumberValue(ticks - (ticks % 10));
        }
    }
}
