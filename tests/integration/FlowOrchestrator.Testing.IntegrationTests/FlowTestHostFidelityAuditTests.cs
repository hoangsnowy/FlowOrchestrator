using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Audit (4th sweep) — cases where <see cref="FlowTestHost"/> did not behave like the production
/// engine + InMemory runtime it claims to mirror, plus a metric-cardinality defect that the real
/// engine exhibited on ForEach fan-out. Each test failed against the audited commit.
/// </summary>
public sealed class FlowTestHostFidelityAuditTests
{
    /// <summary>
    /// <c>WithFastPolling()</c> swapped the runtime store for <c>PermissiveRuntimeStore</c>, which did not
    /// override the 5-argument <c>RecordSkippedStepAsync</c> overload. The interface's default method
    /// drops the <c>evaluationTraceJson</c> argument, so a When-skipped step lost its "why skipped"
    /// trace under the test host while production (InMemory / SQL) persists it.
    /// </summary>
    [Fact]
    public async Task WithFastPolling_WhenFalseSkip_PersistsEvaluationTrace_LikeProduction()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WhenGatedFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithFastPolling()
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(body: new { amount = 5 }, timeout: TimeSpan.FromSeconds(15));
        var detail = await host.Services.GetRequiredService<IFlowRunStore>().GetRunDetailAsync(result.RunId);

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(StepStatus.Skipped, result.Steps["gated"].Status);
        var gated = Assert.Single(detail!.Steps!, s => s.StepKey == "gated");
        // The When trace the engine computed, not null (dropped by the DIM default).
        Assert.False(string.IsNullOrWhiteSpace(gated.EvaluationTraceJson));
    }

    /// <summary>
    /// The engine closes a run whose every leaf was When-skipped with run status <c>"Skipped"</c>
    /// (<c>RunTerminationClassifier</c>). <c>FlowTestHost</c>'s poller only treats
    /// Succeeded / Failed / Cancelled / TimedOut as terminal and <see cref="RunStatus"/> had no
    /// Skipped member, so the test host kept polling a finished run until the timeout and then
    /// reported it as <see cref="RunStatus.Running"/> with <c>TimedOut = true</c>.
    /// </summary>
    [Fact]
    public async Task RunWhoseOnlyLeafIsWhenSkipped_IsReportedAsTerminal_NotTimedOut()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WhenOnlyLeafFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(body: new { amount = 5 }, timeout: TimeSpan.FromSeconds(5));
        var stored = await host.Services.GetRequiredService<IFlowRunStore>().GetRunAsync(result.RunId);

        // Assert
        Assert.Equal("Skipped", stored!.Status);          // the engine DID finish the run
        // The run is terminal: no host timeout, and it reports the engine's Skipped status.
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Skipped, result.Status);
    }

    /// <summary>
    /// <c>WithFastPolling()</c> replaced the execute-time claim guard with one that always granted the
    /// claim, so a duplicate delivery of the same step executed twice under the test host while the
    /// production InMemory store lets exactly one through ("dispatch many, execute once").
    /// The comment justifying the swap ("the claim guard never releases after a Pending result") was
    /// stale: the engine calls <c>ReleaseStepClaimAsync</c> on the Pending path.
    /// </summary>
    [Fact]
    public async Task WithFastPolling_DuplicateClaimForSameStep_IsRejected_LikeProduction()
    {
        // Arrange
        await using var host = await FlowTestHost.For<LinearTestFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithFastPolling()
            .BuildAsync();
        var runtime = host.Services.GetRequiredService<IFlowRunRuntimeStore>();
        var runId = Guid.NewGuid();

        // Act
        var first = await runtime.TryClaimStepAsync(runId, "step1");
        var second = await runtime.TryClaimStepAsync(runId, "step1");

        // Assert
        Assert.True(first);
        // Claim exclusion holds, as in production.
        Assert.False(second);
    }

    /// <summary>
    /// The dashboard webhook endpoint never captures credential headers into the run; the test host's
    /// webhook trigger must behave the same, or a flow that reads them passes under test and fails live.
    /// </summary>
    [Fact]
    public async Task TriggerWebhookAsync_strips_credential_headers_like_the_webhook_endpoint()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WebhookFlow>()
            .WithHandler<CaptureHeaderStepHandler>("CaptureHeader")
            .BuildAsync();
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer secret",
            ["Cookie"] = "session=abc",
            ["X-Webhook-Key"] = "shared",
            ["X-Tenant"] = "acme",
        };

        // Act
        var result = await host.TriggerWebhookAsync("demo-hook", body: new { }, headers: headers, timeout: TimeSpan.FromSeconds(30));
        var stored = await host.Services.GetRequiredService<IOutputsRepository>().GetTriggerHeadersAsync(result.RunId);

        // Assert
        Assert.False(result.TimedOut);
        Assert.NotNull(stored);
        Assert.Equal("acme", stored!["X-Tenant"]);
        Assert.False(stored.ContainsKey("Authorization"));
        Assert.False(stored.ContainsKey("Cookie"));
        Assert.False(stored.ContainsKey("X-Webhook-Key"));
    }

    /// <summary>
    /// The engine tagged <c>flow_step_duration_ms</c> (and queue-delay / claim-lost / poll-attempt
    /// instruments) with the RUNTIME step key. ForEach children are keyed
    /// <c>{loop}.{index}.{child}</c>, so every iteration index became a new metric time series:
    /// unbounded cardinality proportional to collection size. OTel SDKs cap a metric at 2,000
    /// series by default, after which points fold into an overflow series and per-step metrics go
    /// wrong for every flow.
    /// </summary>
    [Fact]
    public async Task ForEachFanOut_StepDurationMetric_DoesNotEmitOneSeriesPerIteration()
    {
        // Arrange
        var stepKeys = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == FlowOrchestratorTelemetry.SourceName && instrument.Name == "flow_step_duration_ms")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "step_key" && tag.Value is string key && key.StartsWith("process_items.", StringComparison.Ordinal))
                    stepKeys.TryAdd(key, 0);
            }
        });
        listener.Start();

        await using var host = await FlowTestHost.For<ForEachTestFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();
        var items = Enumerable.Range(0, 25).Select(i => "item" + i).ToArray();

        // Act
        var result = await host.TriggerAsync(body: new { items }, timeout: TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(RunStatus.Succeeded, result.Status);
        // The loop child is reported under one bounded key (the manifest key), not one per iteration.
        Assert.True(stepKeys.Count <= 1, $"step_key tag produced {stepKeys.Count} distinct series for one loop child: {string.Join(", ", stepKeys.Keys.Take(5))}…");
    }
}

/// <summary>
/// Flow whose <c>gated</c> step is skipped by a When clause that is false for small amounts (the engine
/// records it <see cref="StepStatus.Skipped"/> with a When evaluation trace) while the sibling leaf
/// <c>tail</c> succeeds, so the run itself ends Succeeded.
/// </summary>
public sealed class WhenGatedFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("a0d17000-0000-0000-0000-000000000001");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary><c>start</c> then <c>gated</c> (When <c>amount &gt; 1000</c>) and <c>tail</c>.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["start"] = new StepMetadata
            {
                Type = "Echo",
                Inputs = new Dictionary<string, object?> { ["label"] = "start" }
            },
            ["gated"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection
                {
                    ["start"] = new RunAfterCondition
                    {
                        Statuses = [StepStatus.Succeeded],
                        When = "@triggerBody().amount > 1000"
                    }
                },
                Inputs = new Dictionary<string, object?> { ["label"] = "gated" }
            },
            ["tail"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["start"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "tail" }
            }
        }
    };
}

/// <summary>
/// Two-step flow whose only leaf is gated by a When clause that is false for small amounts, so the
/// engine terminates the run with status <c>"Skipped"</c>.
/// </summary>
public sealed class WhenOnlyLeafFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("a0d17000-0000-0000-0000-000000000002");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary><c>start</c> then <c>gated</c> (When <c>amount &gt; 1000</c>).</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["start"] = new StepMetadata
            {
                Type = "Echo",
                Inputs = new Dictionary<string, object?> { ["label"] = "start" }
            },
            ["gated"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection
                {
                    ["start"] = new RunAfterCondition
                    {
                        Statuses = [StepStatus.Succeeded],
                        When = "@triggerBody().amount > 1000"
                    }
                },
                Inputs = new Dictionary<string, object?> { ["label"] = "gated" }
            }
        }
    };
}
