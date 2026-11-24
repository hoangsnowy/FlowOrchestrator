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
