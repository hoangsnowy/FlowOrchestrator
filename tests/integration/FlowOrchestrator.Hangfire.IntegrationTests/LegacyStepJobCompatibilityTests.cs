using System.Collections.Concurrent;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// Step jobs that FlowOrchestrator 1.34.x left in a Hangfire queue, replayed on 2.0 exactly as a 2.0
/// worker meets them: the stored invocation and argument JSON (captured from a 1.34.7 + Hangfire.SqlServer
/// database by the upgrade rehearsal, <c>tests/upgrade</c>) deserialised by Hangfire's own
/// <see cref="InvocationData"/>, then invoked on the registered <see cref="IHangfireStepRunner"/>.
/// </summary>
/// <remarks>
/// 1.x re-dispatched a <see cref="StepStatus.Pending"/> step (poll re-check, <c>WaitForSignal</c>
/// deadline check) with its resolved inputs, which were <see cref="JsonElement"/> values; Newtonsoft
/// stores only their <c>ValueKind</c>, so they come back as <c>default(JsonElement)</c>. Without a repair
/// the engine throws while recording the step start, Hangfire retries the job until it fails, and the
/// run stays Running for ever.
/// </remarks>
[Collection(nameof(HangfireIntegrationGlobalFiltersCollection))]
public sealed class LegacyStepJobCompatibilityTests : IAsyncDisposable
{
    private const string StepRunnerType = "FlowOrchestrator.Hangfire.IHangfireStepRunner, FlowOrchestrator.Hangfire";

    private const string LegacyParameterTypes =
        """["FlowOrchestrator.Core.Execution.IExecutionContext, FlowOrchestrator.Core","System.Guid, mscorlib","FlowOrchestrator.Core.Execution.IStepInstance, FlowOrchestrator.Core","Hangfire.Server.PerformContext, Hangfire.Core"]""";

    private static readonly string[] _destroyed =
    [
        "pollEnabled", "pollIntervalSeconds", "pollTimeoutSeconds", "pollMinAttempts",
        "pollConditionPath", "pollConditionEquals", "__pollStartedAtUtc", "__pollAttempt",
    ];

    private readonly ServiceProvider _provider;
    private readonly CarrierCalls _calls = new();
    private readonly InMemoryStorage _storage = new();

    public LegacyStepJobCompatibilityTests()
    {
        // What the customer's AddHangfire(c => c.UseRecommendedSerializerSettings()) sets: job arguments are
        // read with type-name handling, which is how 1.x jobs carried their interface-typed arguments.
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();
        var storage = _storage;
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IBackgroundJobClient>(new BackgroundJobClient(storage));
        services.AddSingleton<IRecurringJobManager>(new RecurringJobManager(storage));
        services.AddSingleton(_calls);
        services.AddFlowOrchestrator(options =>
        {
            options.UseInMemory();
            options.UseHangfire();
            options.AddFlow<LegacyShipmentFlow>();
            options.AddFlow<LegacyShipmentBatchFlow>();
            options.AddFlow<LegacyApprovalFlow>();
        });
        services.AddStepHandler<CarrierStatusStep>("CarrierStatus");
        _provider = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        _storage.Dispose();
    }

    [Fact]
    public async Task LegacyPollRecheck_WithDestroyedInputs_ResolvesManifestInputsAndCompletesTheStep()
    {
        // Arrange — a run whose 1.34 poll re-check job is still queued: inputs as 1.34.7 stored them.
        var runId = await TriggerAsync(new LegacyShipmentFlow(), """{"shipmentId":"S1"}""");
        _calls.Delivered.Add("S1");
        var job = DeserializeLegacyJob(runId, LegacyShipmentFlow.FlowId, "track",
            """{"shipmentId":{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":3},"__loopItem":null,""" + DestroyedPollInputs + "}");

        // Act
        await PerformAsync(job);

        // Assert
        Assert.Equal(["S1"], _calls.ShipmentIds);
        Assert.Equal(StepStatus.Succeeded, await GetStepStatusAsync(runId, "track"));
    }

    [Fact]
    public async Task LegacyForEachPollRecheck_WithDestroyedLoopItem_RecomputesTheItemFromTheTriggerBody()
    {
        // Arrange — iteration 1 of a ForEach whose 1.34 poll re-check is queued; __loopItem was destroyed.
        var runId = await TriggerAsync(new LegacyShipmentBatchFlow(), """{"shipments":["B1","B2","B3"]}""");
        _calls.Delivered.Add("B2");
        var job = DeserializeLegacyJob(runId, LegacyShipmentBatchFlow.FlowId, "track_all.1.track",
            """{"shipmentId":null,"__loopItem":{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":3},""" + DestroyedPollInputs + "}",
            index: 1);

        // Act
        await PerformAsync(job);

        // Assert
        Assert.Equal(["B2"], _calls.ShipmentIds);
        Assert.Equal(StepStatus.Succeeded, await GetStepStatusAsync(runId, "track_all.1.track"));
    }

    [Fact]
    public async Task LegacyWaitForSignalCheck_WithDestroyedInputs_RunsTheStepInsteadOfThrowing()
    {
        // Arrange — the 1.34 deadline check of a parked WaitForSignal step (signalName / timeoutSeconds destroyed).
        var runId = await TriggerAsync(new LegacyApprovalFlow(), """{"requestId":"REQ-B","timeoutSeconds":150}""");
        var job = DeserializeLegacyJob(runId, LegacyApprovalFlow.FlowId, "wait_for_approval",
            """{"signalName":{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":3},"timeoutSeconds":{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":4}}""");

        // Act
        await PerformAsync(job);

        // Assert — the step parks again on the manifest's signal name instead of failing the job.
        Assert.Equal(StepStatus.Pending, await GetStepStatusAsync(runId, "wait_for_approval"));
    }

    [Fact]
    public void Repair_DropsUndeclaredDestroyedInputs_AndRestoresDeclaredOnesFromTheManifest()
    {
        // Arrange
        var flow = new LegacyShipmentFlow();
        var undefined = default(JsonElement);
        var step = new StepInstance("track", "CarrierStatus")
        {
            Inputs = new Dictionary<string, object?>
            {
                ["shipmentId"] = undefined,
                ["pollIntervalSeconds"] = undefined,
                ["__pollAttempt"] = undefined,
                ["__loopItem"] = null,
                ["kept"] = "plain value",
            },
        };
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { TriggerData = undefined };

        // Act
        var repaired = LegacyStepJobRepair.Repair(ctx, step, flow);

        // Assert
        Assert.Equal(3, repaired);
        Assert.Null(ctx.TriggerData);
        Assert.Equal("@triggerBody()?.shipmentId", step.Inputs["shipmentId"]);
        Assert.Equal(30, step.Inputs["pollIntervalSeconds"]);
        Assert.False(step.Inputs.ContainsKey("__pollAttempt"));
        Assert.Equal("plain value", step.Inputs["kept"]);
    }

    [Fact]
    public async Task PollRecheck_QueuedBy1348_DecodesTheCarriedInputs_AndHonoursItsPollBookkeeping()
    {
        // Arrange — 1.34.8 carries the resolved inputs as one System.Text.Json string; nothing is destroyed.
        // pollMinAttempts = 3 exists only in the carried inputs (the manifest default is 1), and the
        // carried attempt counter says this is attempt 2, so a delivered shipment must still poll again.
        var runId = await TriggerAsync(new LegacyShipmentFlow(), """{"shipmentId":"S8"}""");
        _calls.Delivered.Add("S8");
        var carried = $$"""{"shipmentId":"S8","pollEnabled":true,"pollIntervalSeconds":30,"pollTimeoutSeconds":3600,"pollMinAttempts":3,"pollConditionPath":"status","pollConditionEquals":"delivered","__pollStartedAtUtc":"{{DateTimeOffset.UtcNow:O}}","__pollAttempt":1}""";
        var job = DeserializeLegacyJob(runId, LegacyShipmentFlow.FlowId, "track",
            "{\"__loopItem\":null,\"" + LegacyStepJobRepair.EncodedInputsKey + "\":" + JsonSerializer.Serialize(carried) + "}");

        // Act
        await PerformAsync(job);

        // Assert — without the decode the manifest filled the shipment id back in, but pollMinAttempts and the
        // attempt count were gone, so the delivered shipment did not stay Pending.
        Assert.Equal(["S8"], _calls.ShipmentIds);
        Assert.Equal(StepStatus.Pending, await GetStepStatusAsync(runId, "track"));
    }

    [Fact]
    public void Repair_1348EncodedInputs_RestoresTheExactValues_AndRemovesTheReservedKey()
    {
        // Arrange
        var flow = new LegacyShipmentBatchFlow();
        var step = new StepInstance("track_all.1.track", "CarrierStatus")
        {
            Inputs = new Dictionary<string, object?>
            {
                ["kept"] = "plain value",
                [LegacyStepJobRepair.EncodedInputsKey] = """{"__loopItem":{"id":"B2","qty":2},"pollEnabled":true,"__pollAttempt":4}""",
            },
        };
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext();

        // Act
        var repaired = LegacyStepJobRepair.Repair(ctx, step, flow);

        // Assert
        Assert.Equal(3, repaired);
        Assert.False(step.Inputs.ContainsKey(LegacyStepJobRepair.EncodedInputsKey));
        Assert.Equal("plain value", step.Inputs["kept"]);
        var item = Assert.IsType<JsonElement>(step.Inputs["__loopItem"]);
        Assert.Equal("B2", item.GetProperty("id").GetString());
        Assert.True(Assert.IsType<JsonElement>(step.Inputs["pollEnabled"]).GetBoolean());
        Assert.Equal(4, Assert.IsType<JsonElement>(step.Inputs["__pollAttempt"]).GetInt32());
    }

    private static string DestroyedPollInputs => string.Join(",",
        _destroyed.Select(k => "\"" + k + "\":" + DestroyedElement));

    /// <summary>A <see cref="JsonElement"/> as Newtonsoft.Json wrote it into a 1.x job: the type and ValueKind only.</summary>
    private const string DestroyedElement = """{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":4}""";

    private async Task<Guid> TriggerAsync(IFlowDefinition flow, string body)
    {
        var engine = _provider.GetRequiredService<IFlowOrchestrator>();
        using var doc = JsonDocument.Parse(body);
        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", doc.RootElement.Clone(), new Dictionary<string, string>()),
        };
        await engine.TriggerAsync(ctx);
        return ctx.RunId;
    }

    /// <summary>Builds the job exactly as 1.34.7 stored it and lets Hangfire deserialise it.</summary>
    private static Job DeserializeLegacyJob(Guid runId, Guid flowId, string stepKey, string inputsJson, int index = 0)
    {
        // The context argument: a TriggerContext with destroyed trigger data and the whole manifest under
        // "Flow", typed with the customer's flow class (not loadable here; 2.0 ignores the property).
        var context = """
            {"$type":"FlowOrchestrator.Core.Execution.TriggerContext, FlowOrchestrator.Core","RunId":"__RUN_ID__","TriggerData":{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":1},"TriggerHeaders":{"$type":"System.Collections.Generic.Dictionary`2[[System.String],[System.String]], mscorlib","Content-Type":"application/json"},"Flow":{"$type":"OrderHub.ShipmentBatchFlow, OrderHub","Id":"__FLOW_ID__","Version":"1.0","Manifest":{"Triggers":{"manual":{"Inputs":{}}},"Steps":{"close_batch":{"Type":"LogMessage","RunAfter":{"track_all":{"statuses":["Succeeded"]}},"Inputs":{"message":"batch delivered"}}}}},"Trigger":{"$type":"FlowOrchestrator.Core.Execution.Trigger, FlowOrchestrator.Core","Key":"manual","Type":"Manual","Data":{"$type":"System.Text.Json.JsonElement, System.Text.Json"},"Headers":{"$type":"System.Collections.Generic.Dictionary`2[[System.String],[System.String]], mscorlib","Content-Type":"application/json"}},"JobId":"30"}
            """;
        var step = """
            {"$type":"FlowOrchestrator.Core.Execution.StepInstance, FlowOrchestrator.Core","RunId":"__RUN_ID__","TriggerData":{"$type":"System.Text.Json.JsonElement, System.Text.Json"},"TriggerHeaders":{"$type":"System.Collections.Generic.Dictionary`2[[System.String],[System.String]], mscorlib","Content-Type":"application/json"},"ScheduledTime":"2026-11-24T08:09:34.3976505+00:00","Type":"__STEP_TYPE__","Key":"__STEP_KEY__","Inputs":__INPUTS__,"Index":__INDEX__}
            """;
        context = context.Replace("__RUN_ID__", runId.ToString(), StringComparison.Ordinal)
            .Replace("__FLOW_ID__", flowId.ToString(), StringComparison.Ordinal);
        step = step.Replace("__RUN_ID__", runId.ToString(), StringComparison.Ordinal)
            .Replace("__STEP_TYPE__", stepKey.EndsWith("track", StringComparison.Ordinal) ? "CarrierStatus" : "WaitForSignal", StringComparison.Ordinal)
            .Replace("__STEP_KEY__", stepKey, StringComparison.Ordinal)
            .Replace("__INPUTS__", inputsJson, StringComparison.Ordinal)
            .Replace("__INDEX__", index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var arguments = JsonSerializer.Serialize(new[] { context, JsonSerializer.Serialize(flowId.ToString()), step, null });
        return new InvocationData(StepRunnerType, "RunStepAsync", LegacyParameterTypes, arguments).DeserializeJob();
    }

    private async Task PerformAsync(Job job)
    {
        Assert.Equal(typeof(IHangfireStepRunner), job.Type);
        using var scope = _provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IHangfireStepRunner>();
        var result = job.Method.Invoke(runner, job.Args.ToArray());
        await (ValueTask<object?>)result!;
    }

    private async Task<StepStatus?> GetStepStatusAsync(Guid runId, string stepKey)
    {
        var run = await _provider.GetRequiredService<IFlowRunStore>().GetRunDetailAsync(runId);
        var status = run?.Steps?.FirstOrDefault(s => s.StepKey == stepKey)?.Status;
        return status is null ? null : Enum.Parse<StepStatus>(status, ignoreCase: true);
    }

    /// <summary>Records the shipment ids the carrier step saw; reports delivered for the ids in <see cref="Delivered"/>.</summary>
    public sealed class CarrierCalls
    {
        public ConcurrentBag<string> Delivered { get; } = [];
        public List<string> ShipmentIds { get; } = [];
    }

    public sealed class CarrierStatusStep(CarrierCalls calls) : PollableStepHandler<CarrierStatusInput>
    {
        protected override ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
            IExecutionContext ctx, IFlowDefinition flow, IStepInstance<CarrierStatusInput> step)
        {
            var id = step.Inputs.ShipmentId ?? (step.Inputs.LoopItem is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : step.Inputs.LoopItem?.ToString());
            lock (calls.ShipmentIds)
            {
                calls.ShipmentIds.Add(id ?? "<null>");
            }
            var status = id is not null && calls.Delivered.Contains(id) ? "delivered" : "in_transit";
            return ValueTask.FromResult((JsonSerializer.SerializeToElement(new { status }), true));
        }
    }

    public sealed class CarrierStatusInput : IPollableInput
    {
        public string? ShipmentId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("__loopItem")]
        public object? LoopItem { get; set; }

        public bool PollEnabled { get; set; }
        public int PollIntervalSeconds { get; set; } = 30;
        public int PollTimeoutSeconds { get; set; } = 3600;
        public int PollMinAttempts { get; set; } = 1;
        public string? PollConditionPath { get; set; }
        public object? PollConditionEquals { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("__pollStartedAtUtc")]
        public string? PollStartedAtUtc { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("__pollAttempt")]
        public int? PollAttempt { get; set; }
    }

    private static Dictionary<string, object?> PollInputs() => new()
    {
        ["pollEnabled"] = true,
        ["pollIntervalSeconds"] = 30,
        ["pollTimeoutSeconds"] = 3600,
        ["pollConditionPath"] = "status",
        ["pollConditionEquals"] = "delivered",
    };

    public sealed class LegacyShipmentFlow : IFlowDefinition
    {
        public static readonly Guid FlowId = new("1e9ac700-0000-0000-0000-000000000005");
        public Guid Id => FlowId;
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new()
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["track"] = new StepMetadata
                {
                    Type = "CarrierStatus",
                    Inputs = new Dictionary<string, object?>(PollInputs()) { ["shipmentId"] = "@triggerBody()?.shipmentId" },
                },
            },
        };
    }

    public sealed class LegacyShipmentBatchFlow : IFlowDefinition
    {
        public static readonly Guid FlowId = new("1e9ac700-0000-0000-0000-000000000006");
        public Guid Id => FlowId;
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new()
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["track_all"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = "@triggerBody()?.shipments",
                    ConcurrencyLimit = 1,
                    Steps = new StepCollection { ["track"] = new StepMetadata { Type = "CarrierStatus", Inputs = PollInputs() } },
                },
            },
        };
    }

    public sealed class LegacyApprovalFlow : IFlowDefinition
    {
        public static readonly Guid FlowId = new("1e9ac700-0000-0000-0000-000000000004");
        public Guid Id => FlowId;
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new()
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["wait_for_approval"] = new StepMetadata
                {
                    Type = "WaitForSignal",
                    Inputs = new Dictionary<string, object?>
                    {
                        ["signalName"] = "approval",
                        ["timeoutSeconds"] = "@triggerBody()?.timeoutSeconds",
                    },
                },
            },
        };
    }
}
