using System.Collections.Concurrent;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
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
/// Pending re-checks on the Hangfire runtime, driven through a real <see cref="BackgroundJobServer"/> over
/// Hangfire.InMemory so every step job's arguments go through Hangfire's Newtonsoft.Json argument store
/// exactly as in production.
/// </summary>
/// <remarks>
/// A step that returns <see cref="StepStatus.Pending"/> is re-dispatched with the inputs its attempt
/// resolved. Those are <see cref="JsonElement"/> values (an expression over the trigger body, and every
/// input a typed handler writes back), and Newtonsoft.Json keeps only a <see cref="JsonElement"/>'s
/// <c>ValueKind</c>: up to 1.34.7 the re-check job came back with <c>default(JsonElement)</c> inputs.
/// </remarks>
[Collection(nameof(HangfireIntegrationGlobalFiltersCollection))]
public sealed class PendingRecheckInputsTests : IDisposable
{
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(60);

    private const string StepRunnerType = "FlowOrchestrator.Hangfire.IHangfireStepRunner, FlowOrchestrator.Hangfire";

    private const string RunStepParameterTypes =
        """["FlowOrchestrator.Core.Execution.IExecutionContext, FlowOrchestrator.Core","System.Guid, mscorlib","FlowOrchestrator.Core.Execution.IStepInstance, FlowOrchestrator.Core","Hangfire.Server.PerformContext, Hangfire.Core"]""";

    /// <summary>A <see cref="JsonElement"/> as Newtonsoft.Json wrote it into a 1.34.7 job: the type and ValueKind only.</summary>
    private const string DestroyedElement = """{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":3}""";

    private readonly InMemoryStorage _storage = new();
    private readonly ServiceProvider _provider;
    private BackgroundJobServer? _server;
    private readonly CarrierCalls _calls = new();
    private readonly RunCompletions _completions = new();

    public PendingRecheckInputsTests()
    {
        // What the documented AddHangfire(c => c.UseRecommendedSerializerSettings()) sets.
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IBackgroundJobClient>(new BackgroundJobClient(_storage));
        services.AddSingleton<IRecurringJobManager>(new RecurringJobManager(_storage));
        services.AddSingleton(_calls);
        services.AddSingleton<IFlowEventNotifier>(_completions);
        services.AddFlowOrchestrator(options =>
        {
            options.UseInMemory();
            options.UseHangfire();
            options.AddFlow<ShipmentFlow>();
            options.AddFlow<StaticShipmentFlow>();
            options.AddFlow<ShipmentBatchFlow>();
            options.AddFlow<ApprovalFlow>();
        });
        services.AddStepHandler<CarrierStatusStep>("CarrierStatus");
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _server?.Dispose();
        _provider.Dispose();
        _storage.Dispose();
    }

    /// <summary>Starts a real Hangfire server over the test storage; legacy-job tests run without one.</summary>
    private void StartServer()
    {
        _server = new BackgroundJobServer(
            new BackgroundJobServerOptions
            {
                WorkerCount = 2,
                ServerName = "pending-recheck-tests",
                SchedulePollingInterval = TimeSpan.FromMilliseconds(200),
                Activator = new ScopedJobActivator(_provider.GetRequiredService<IServiceScopeFactory>()),
            },
            _storage);
    }

    [Fact]
    public async Task PollRecheck_InputFromTriggerBodyExpression_SecondAttemptSeesTheResolvedValue()
    {
        // Arrange — delivered on the second attempt, so the second attempt is the Pending re-check job.
        _calls.DeliverOnAttempt = 2;
        var runId = Guid.NewGuid();
        var completion = _completions.For(runId);
        StartServer();

        // Act
        await TriggerAsync(runId, new ShipmentFlow(), """{"shipmentId":"S1","minAttempts":1,"timeoutSeconds":3600}""");
        var status = await completion.WaitAsync(_budget);

        // Assert
        var attempts = _calls.For("track");
        Assert.Equal(2, attempts.Count);
        Assert.Equal("S1", attempts[1].ShipmentId);
        Assert.True(attempts[1].PollEnabled);
        Assert.Equal("Succeeded", status);
        Assert.Equal("Succeeded", await GetStepStatusAsync(runId, "track"));
    }

    [Fact]
    public async Task PollRecheck_CarriesPollBookkeeping_AttemptCountAndStartTimeSurviveEveryRecheck()
    {
        // Arrange — pollMinAttempts = 3: only the third attempt may succeed, which needs __pollAttempt carried.
        _calls.DeliverOnAttempt = 1;
        var runId = Guid.NewGuid();
        var completion = _completions.For(runId);
        StartServer();

        // Act
        await TriggerAsync(runId, new ShipmentFlow(), """{"shipmentId":"S2","minAttempts":3,"timeoutSeconds":3600}""");
        var status = await completion.WaitAsync(_budget);

        // Assert
        var attempts = _calls.For("track");
        Assert.Equal(3, attempts.Count);
        Assert.Equal([null, 1, 2], attempts.Select(a => a.PollAttemptBefore));
        Assert.Null(attempts[0].PollStartedAtUtc);
        Assert.NotNull(attempts[1].PollStartedAtUtc);
        Assert.Equal(attempts[1].PollStartedAtUtc, attempts[2].PollStartedAtUtc);
        Assert.EndsWith("+00:00", attempts[1].PollStartedAtUtc, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(attempts[1].PollStartedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out _));
        Assert.All(attempts, a => Assert.Equal("S2", a.ShipmentId));
        Assert.Equal("Succeeded", status);
    }

    [Fact]
    public async Task PollRecheck_StaticLiteralInputs_TypedHandlerWriteBackSurvives_MinAttemptsAreHonoured()
    {
        // Arrange — ShipmentTrackingFlow's shape: literal inputs only, pollMinAttempts = 3, condition met at once.
        // The typed handler writes its literals back as JsonElement values, so 1.34.7 destroyed them too.
        _calls.DeliverOnAttempt = 1;
        var runId = Guid.NewGuid();
        var completion = _completions.For(runId);
        StartServer();

        // Act
        await TriggerAsync(runId, new StaticShipmentFlow(), "{}");
        var status = await completion.WaitAsync(_budget);

        // Assert
        var attempts = _calls.For("track");
        Assert.Equal(3, attempts.Count);
        Assert.All(attempts, a => Assert.Equal("STATIC-1", a.ShipmentId));
        Assert.All(attempts, a => Assert.True(a.PollEnabled));
        Assert.Equal("Succeeded", status);
    }

    [Fact]
    public async Task PollRecheck_PollTimeoutWindowIsEnforced_TheRecheckFailsTheStepInsteadOfSucceeding()
    {
        // Arrange — never delivered; pollTimeoutSeconds = 1 with a 1 s interval, so the first re-check is past the window.
        _calls.DeliverOnAttempt = int.MaxValue;
        var runId = Guid.NewGuid();
        var completion = _completions.For(runId);
        StartServer();

        // Act
        await TriggerAsync(runId, new ShipmentFlow(), """{"shipmentId":"S3","minAttempts":1,"timeoutSeconds":1}""");
        var status = await completion.WaitAsync(_budget);

        // Assert
        var step = await GetStepAsync(runId, "track");
        // Hangfire schedules to the second, so the deadline may need one more re-check; never fewer than two.
        var attempts = _calls.For("track");
        Assert.True(attempts.Count >= 2, $"expected at least two attempts, saw {attempts.Count}");
        Assert.All(attempts, a => Assert.True(a.PollEnabled));
        Assert.Equal("Failed", status);
        Assert.Equal("Failed", step?.Status);
        Assert.Contains("Polling timed out after 1 seconds.", step?.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForEachChildPollRecheck_EachIterationsRecheckSeesItsOwnLoopItem()
    {
        // Arrange
        _calls.DeliverOnAttempt = 2;
        var runId = Guid.NewGuid();
        var completion = _completions.For(runId);
        StartServer();

        // Act
        await TriggerAsync(runId, new ShipmentBatchFlow(), """{"shipments":["B1","B2"]}""");
        var status = await completion.WaitAsync(_budget);

        // Assert
        var first = _calls.For("track_all.0.track");
        var second = _calls.For("track_all.1.track");
        Assert.Equal(["B1", "B1"], first.Select(a => a.LoopItem));
        Assert.Equal(["B2", "B2"], second.Select(a => a.LoopItem));
        Assert.Equal("Succeeded", status);
    }

    [Fact]
    public async Task WaitForSignalTimeoutRecheck_InputsSurvive_TheStepTimesOutOnItsOwnSignalName()
    {
        // Arrange — timeoutSeconds resolved from the trigger body; nobody sends the signal.
        var runId = Guid.NewGuid();
        var completion = _completions.For(runId);
        StartServer();

        // Act
        await TriggerAsync(runId, new ApprovalFlow(), """{"timeoutSeconds":1}""");
        var status = await completion.WaitAsync(_budget);

        // Assert
        var step = await GetStepAsync(runId, "wait_for_approval");
        Assert.Equal("Failed", status);
        Assert.Equal("Failed", step?.Status);
        Assert.Equal("Signal 'approval' not received within 1s.", step?.ErrorMessage);
    }

    [Fact]
    public async Task PollRecheckJob_StoredArguments_CarryTheResolvedInputsAsPlainJson()
    {
        // Arrange — the first attempt runs, the re-check job is left in the schedule.
        var runId = Guid.NewGuid();
        await StartParkedRunAsync(runId, new ShipmentFlow(), """{"shipmentId":"A1","minAttempts":1,"timeoutSeconds":3600}""", "track");
        var scheduled = Assert.Single(_storage.GetMonitoringApi().ScheduledJobs(0, 10));

        // Act
        using var connection = _storage.GetConnection();
        var arguments = connection.GetJobData(scheduled.Key)!.InvocationData!.Arguments;

        // Assert — 1.34.7 stored every input here as {"$type":"System.Text.Json.JsonElement…","ValueKind":n}.
        using var args = JsonDocument.Parse(arguments);
        using var stepArgument = JsonDocument.Parse(args.RootElement[2].GetString()!);
        var inputs = stepArgument.RootElement.GetProperty("Inputs");
        Assert.DoesNotContain("JsonElement", inputs.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("A1", inputs.GetProperty("shipmentId").GetString());
        Assert.True(inputs.GetProperty("pollEnabled").GetBoolean());
        Assert.Equal(1, inputs.GetProperty("__pollAttempt").GetInt32());
    }

    [Fact]
    public async Task LegacyPollRecheckJob_QueuedBy1347WithDestroyedInputs_CompletesTheStepWithManifestInputs()
    {
        // Arrange — a run parked on a poll whose re-check job 1.34.7 stored with every input destroyed.
        _calls.DeliverOnAttempt = 1;
        var runId = Guid.NewGuid();
        await StartParkedRunAsync(runId, new ShipmentFlow(), """{"shipmentId":"L1","minAttempts":1,"timeoutSeconds":3600}""", "track");
        var job = DeserializeLegacyJob(runId, ShipmentFlow.FlowId, "track", "CarrierStatus",
            "{" + string.Join(",", new[]
            {
                "shipmentId", "pollEnabled", "pollIntervalSeconds", "pollTimeoutSeconds",
                "pollMinAttempts", "pollConditionPath", "pollConditionEquals", "__pollStartedAtUtc", "__pollAttempt",
            }.Select(k => "\"" + k + "\":" + DestroyedElement)) + ",\"__loopItem\":null}");

        // Act
        await PerformAsync(job);

        // Assert
        var attempt = Assert.Single(_calls.For("track"));
        Assert.Equal("L1", attempt.ShipmentId);
        Assert.True(attempt.PollEnabled);
        Assert.Null(attempt.PollAttemptBefore);
        Assert.Equal("Succeeded", await GetStepStatusAsync(runId, "track"));
    }

    [Fact]
    public async Task LegacyForEachPollRecheckJob_QueuedBy1347WithDestroyedLoopItem_RecomputesTheItem()
    {
        // Arrange
        _calls.DeliverOnAttempt = 1;
        var runId = Guid.NewGuid();
        await StartParkedRunAsync(runId, new ShipmentBatchFlow(), """{"shipments":["L1","L2"]}""", "track_all.1.track");
        var job = DeserializeLegacyJob(runId, ShipmentBatchFlow.FlowId, "track_all.1.track", "CarrierStatus",
            """{"pollEnabled":__D__,"pollIntervalSeconds":__D__,"pollConditionPath":__D__,"pollConditionEquals":__D__,"__pollAttempt":__D__,"__loopItem":__D__,"__loopIndex":1}"""
                .Replace("__D__", DestroyedElement, StringComparison.Ordinal),
            index: 1);

        // Act
        await PerformAsync(job);

        // Assert
        var attempt = Assert.Single(_calls.For("track_all.1.track"));
        Assert.Equal("L2", attempt.LoopItem);
        Assert.True(attempt.PollEnabled);
        Assert.Equal("Succeeded", await GetStepStatusAsync(runId, "track_all.1.track"));
    }

    [Fact]
    public async Task LegacyWaitForSignalRecheckJob_QueuedBy1347WithDestroyedInputs_ParksOnTheManifestSignalName()
    {
        // Arrange — the deadline check of a parked WaitForSignal whose signalName / timeoutSeconds were destroyed.
        var runId = Guid.NewGuid();
        await StartParkedRunAsync(runId, new ApprovalFlow(), """{"timeoutSeconds":600}""", "wait_for_approval");
        var job = DeserializeLegacyJob(runId, ApprovalFlow.FlowId, "wait_for_approval", "WaitForSignal",
            """{"signalName":__D__,"timeoutSeconds":__D__}""".Replace("__D__", DestroyedElement, StringComparison.Ordinal));

        // Act
        await PerformAsync(job);

        // Assert — 1.34.7 failed the step here with "requires a non-empty 'signalName' input".
        var step = await GetStepAsync(runId, "wait_for_approval");
        Assert.Equal("Pending", step?.Status);
        var waiter = await _provider.GetRequiredService<IFlowSignalStore>().GetWaiterAsync(runId, "wait_for_approval");
        Assert.Equal("approval", waiter?.SignalName);
    }

    private async Task TriggerAsync(Guid runId, IFlowDefinition flow, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var ctx = new TriggerContext
        {
            RunId = runId,
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", doc.RootElement.Clone(), new Dictionary<string, string>()),
        };
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>().TriggerAsync(ctx);
    }

    /// <summary>
    /// Starts a run with no Hangfire server and performs its enqueued jobs (deserialised from the storage)
    /// until only scheduled ones remain, so <paramref name="stepKey"/> has parked Pending once and its
    /// re-check sits unrun in the schedule; the legacy job built by the test then stands in for it.
    /// </summary>
    private async Task StartParkedRunAsync(Guid runId, IFlowDefinition flow, string body, string stepKey)
    {
        _calls.DeliverOnAttempt = int.MaxValue;
        await TriggerAsync(runId, flow, body);

        var api = _storage.GetMonitoringApi();
        var performed = new HashSet<string>(StringComparer.Ordinal);
        bool progressed;
        do
        {
            progressed = false;
            foreach (var (jobId, _) in api.EnqueuedJobs("default", 0, 100))
            {
                if (!performed.Add(jobId))
                {
                    continue;
                }

                using var connection = _storage.GetConnection();
                var data = connection.GetJobData(jobId);
                Assert.NotNull(data?.Job);
                await PerformAsync(data.Job);
                progressed = true;
            }
        }
        while (progressed);

        Assert.Equal("Pending", await GetStepStatusAsync(runId, stepKey));
        _calls.Reset();
        _calls.DeliverOnAttempt = 1;
    }

    /// <summary>Builds a step job with the argument JSON 1.34.7 stored and lets Hangfire deserialise it.</summary>
    private static Job DeserializeLegacyJob(Guid runId, Guid flowId, string stepKey, string stepType, string inputsJson, int index = 0)
    {
        var context = """
            {"$type":"FlowOrchestrator.Core.Execution.TriggerContext, FlowOrchestrator.Core","RunId":"__RUN_ID__","TriggerData":{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":1},"TriggerHeaders":{"$type":"System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[System.String, mscorlib]], mscorlib"},"Trigger":{"$type":"FlowOrchestrator.Core.Execution.Trigger, FlowOrchestrator.Core","Key":"manual","Type":"Manual","Data":{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":1}},"JobId":"30"}
            """;
        var step = """
            {"$type":"FlowOrchestrator.Core.Execution.StepInstance, FlowOrchestrator.Core","RunId":"__RUN_ID__","TriggerData":{"$type":"System.Text.Json.JsonElement, System.Text.Json","ValueKind":1},"ScheduledTime":"2026-11-24T08:09:34.3976505+00:00","Type":"__STEP_TYPE__","Key":"__STEP_KEY__","Inputs":__INPUTS__,"Index":__INDEX__}
            """;
        context = context.Replace("__RUN_ID__", runId.ToString(), StringComparison.Ordinal);
        step = step.Replace("__RUN_ID__", runId.ToString(), StringComparison.Ordinal)
            .Replace("__STEP_TYPE__", stepType, StringComparison.Ordinal)
            .Replace("__STEP_KEY__", stepKey, StringComparison.Ordinal)
            .Replace("__INPUTS__", inputsJson, StringComparison.Ordinal)
            .Replace("__INDEX__", index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var arguments = JsonSerializer.Serialize(new[] { context, JsonSerializer.Serialize(flowId.ToString()), step, null });
        return new InvocationData(StepRunnerType, "RunStepAsync", RunStepParameterTypes, arguments).DeserializeJob();
    }

    private async Task PerformAsync(Job job)
    {
        using var scope = _provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService(job.Type);
        var result = job.Method.Invoke(runner, job.Args.ToArray());
        await (ValueTask<object?>)result!;
    }

    private async Task<FlowStepRecord?> GetStepAsync(Guid runId, string stepKey)
    {
        var run = await _provider.GetRequiredService<IFlowRunStore>().GetRunDetailAsync(runId);
        return run?.Steps?.FirstOrDefault(s => s.StepKey == stepKey);
    }

    private async Task<string?> GetStepStatusAsync(Guid runId, string stepKey)
        => (await GetStepAsync(runId, stepKey))?.Status;

    /// <summary>What one carrier-status attempt saw.</summary>
    public sealed record CarrierAttempt(string? ShipmentId, string? LoopItem, bool PollEnabled, int? PollAttemptBefore, string? PollStartedAtUtc);

    /// <summary>Records every carrier-status attempt per runtime step key; reports delivered from <see cref="DeliverOnAttempt"/> on.</summary>
    public sealed class CarrierCalls
    {
        private readonly ConcurrentDictionary<string, List<CarrierAttempt>> _attempts = new(StringComparer.Ordinal);

        /// <summary>The 1-based attempt from which the carrier reports the shipment delivered.</summary>
        public int DeliverOnAttempt { get; set; } = 2;

        /// <summary>Records an attempt and returns its 1-based number.</summary>
        public int Record(string stepKey, CarrierAttempt attempt)
        {
            var list = _attempts.GetOrAdd(stepKey, _ => []);
            int count;
            lock (list)
            {
                list.Add(attempt);
                count = list.Count;
            }

            return count;
        }

        /// <summary>The attempts recorded for <paramref name="stepKey"/>, in order.</summary>
        public IReadOnlyList<CarrierAttempt> For(string stepKey)
        {
            if (!_attempts.TryGetValue(stepKey, out var list))
            {
                return [];
            }

            lock (list)
            {
                return [.. list];
            }
        }

        /// <summary>Forgets every recorded attempt.</summary>
        public void Reset() => _attempts.Clear();
    }

    /// <summary>Completes a task per run when the engine publishes its <see cref="RunCompletedEvent"/>.</summary>
    public sealed class RunCompletions : IFlowEventNotifier
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _runs = new();

        /// <summary>The terminal status of <paramref name="runId"/>, once it completes.</summary>
        public Task<string> For(Guid runId)
            => _runs.GetOrAdd(runId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        /// <inheritdoc/>
        public ValueTask PublishAsync(FlowLifecycleEvent evt, CancellationToken ct = default)
        {
            if (evt is RunCompletedEvent completed)
            {
                _runs.GetOrAdd(completed.RunId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(completed.Status);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A polling carrier-status step: in transit until <see cref="CarrierCalls.DeliverOnAttempt"/>.</summary>
    public sealed class CarrierStatusStep(CarrierCalls calls) : PollableStepHandler<CarrierStatusInput>
    {
        /// <inheritdoc/>
        protected override ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
            IExecutionContext ctx, IFlowDefinition flow, IStepInstance<CarrierStatusInput> step)
        {
            var input = step.Inputs;
            var loopItem = input.LoopItem switch
            {
                null => null,
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
                JsonElement e => "<" + e.ValueKind + ">",
                var other => other.ToString(),
            };
            var attempt = calls.Record(step.Key, new CarrierAttempt(
                input.ShipmentId, loopItem, input.PollEnabled, input.PollAttempt, input.PollStartedAtUtc));
            var status = attempt >= calls.DeliverOnAttempt ? "delivered" : "in_transit";
            return ValueTask.FromResult((JsonSerializer.SerializeToElement(new { status }), true));
        }
    }

    /// <summary>Inputs of <see cref="CarrierStatusStep"/>.</summary>
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
        ["pollIntervalSeconds"] = 1,
        ["pollConditionPath"] = "status",
        ["pollConditionEquals"] = "delivered",
    };

    /// <summary>One polling step whose shipment id, minimum attempts and timeout come from the trigger body.</summary>
    public sealed class ShipmentFlow : IFlowDefinition
    {
        public static readonly Guid FlowId = new("1e9ac700-0000-0000-0000-000000001348");
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
                    Inputs = new Dictionary<string, object?>(PollInputs())
                    {
                        ["shipmentId"] = "@triggerBody()?.shipmentId",
                        ["pollMinAttempts"] = "@triggerBody()?.minAttempts",
                        ["pollTimeoutSeconds"] = "@triggerBody()?.timeoutSeconds",
                    },
                },
            },
        };
    }

    /// <summary>One polling step with literal inputs only, like the sample app's <c>ShipmentTrackingFlow</c>.</summary>
    public sealed class StaticShipmentFlow : IFlowDefinition
    {
        public static readonly Guid FlowId = new("1e9ac700-0000-0000-0000-00000000134b");
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
                    Inputs = new Dictionary<string, object?>(PollInputs())
                    {
                        ["shipmentId"] = "STATIC-1",
                        ["pollMinAttempts"] = 3,
                        ["pollTimeoutSeconds"] = 3600,
                    },
                },
            },
        };
    }

    /// <summary>A ForEach over the trigger body whose child polls on its loop item.</summary>
    public sealed class ShipmentBatchFlow : IFlowDefinition
    {
        public static readonly Guid FlowId = new("1e9ac700-0000-0000-0000-000000001349");
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
                    ConcurrencyLimit = 2,
                    Steps = new StepCollection { ["track"] = new StepMetadata { Type = "CarrierStatus", Inputs = PollInputs() } },
                },
            },
        };
    }

    /// <summary>A WaitForSignal whose timeout comes from the trigger body.</summary>
    public sealed class ApprovalFlow : IFlowDefinition
    {
        public static readonly Guid FlowId = new("1e9ac700-0000-0000-0000-00000000134a");
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

    /// <summary>Activates each job in its own DI scope, as Hangfire.AspNetCore does.</summary>
    private sealed class ScopedJobActivator(IServiceScopeFactory scopes) : JobActivator
    {
        public override JobActivatorScope BeginScope(JobActivatorContext context) => new Scope(scopes.CreateScope());

        private sealed class Scope(IServiceScope scope) : JobActivatorScope
        {
            public override object Resolve(Type type) => scope.ServiceProvider.GetRequiredService(type);

            public override void DisposeScope() => scope.Dispose();
        }
    }
}
