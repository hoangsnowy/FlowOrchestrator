using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Newtonsoft.Json;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// Hangfire step jobs enqueued by FlowOrchestrator 1.17 – 1.34.x target the kept
/// <c>RunStepAsync(IExecutionContext, Guid, IStepInstance, …)</c> overload. Entry steps and every
/// continuation of them were enqueued with the run's <see cref="TriggerContext"/> as the context, so the
/// serialised argument carries <see cref="TriggerContext.Flow"/> with the whole manifest — including
/// <see cref="RunAfterCondition"/> values, which Newtonsoft.Json cannot read since 2.0 removed the
/// Newtonsoft converter from Core. These tests pin that such payloads still deserialise on 2.0.
/// </summary>
public sealed class LegacyStepJobPayloadTests
{
    // Mirrors the job-argument settings of Hangfire's UseRecommendedSerializerSettings(): interface-typed
    // arguments are written with "$type", resolved by simple assembly name.
    private static readonly JsonSerializerSettings HangfireArgumentSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
    };

    private const string RunId = "6f1c2a8e-4b1d-4c8e-9a51-0d6f1b8e2c47";
    private const string FlowId = "0b3c7d1e-9a4f-4e2b-8c6d-5f1a2b3c4d5e";

    private static string LegacyTriggerContextJson(string runAfterValue) => $$"""
        {
          "$type": "FlowOrchestrator.Core.Execution.TriggerContext, FlowOrchestrator.Core",
          "RunId": "{{RunId}}",
          "PrincipalId": "user-1",
          "TriggerData": { "orderId": 42 },
          "TriggerHeaders": { "X-Request-Id": "req-1" },
          "Flow": {
            "$type": "FlowOrchestrator.Hangfire.Tests.LegacyPayloadFlow, FlowOrchestrator.Hangfire.UnitTests",
            "Id": "{{FlowId}}",
            "Version": "1.0",
            "Manifest": {
              "Triggers": {},
              "Steps": {
                "a": { "Type": "Fetch", "RunAfter": {}, "Inputs": {} },
                "b": { "Type": "Ship", "RunAfter": { "a": {{runAfterValue}} }, "Inputs": {} }
              }
            }
          },
          "Trigger": {
            "$type": "FlowOrchestrator.Core.Execution.Trigger, FlowOrchestrator.Core",
            "Key": "manual",
            "Type": "Manual",
            "Data": null,
            "Headers": null
          },
          "SourceRunId": null,
          "JobId": null
        }
        """;

    [Theory]
    [InlineData("""{ "statuses": ["Succeeded"] }""")] // 1.17+ object shape
    [InlineData("""["Succeeded", "Skipped"]""")]       // pre-1.17 array shape
    public void TriggerContextArgumentOfA1xStepJob_Deserialises(string runAfterValue)
    {
        // Arrange
        var json = LegacyTriggerContextJson(runAfterValue);

        // Act
        var ctx = JsonConvert.DeserializeObject<IExecutionContext>(json, HangfireArgumentSettings);

        // Assert
        var trigger = Assert.IsType<TriggerContext>(ctx);
        Assert.Equal(Guid.Parse(RunId), trigger.RunId);
        Assert.Equal("user-1", trigger.PrincipalId);
        Assert.Equal("manual", trigger.Trigger.Key);
        // The flow is not read from the job: the overload resolves it from FlowRegistry by its flowId argument.
        Assert.Null(trigger.Flow);
    }

    [Fact]
    public void TriggerContext_SerialisedByNewtonsoft_DoesNotCarryTheFlowManifest()
    {
        // Arrange
        var ctx = new TriggerContext
        {
            RunId = Guid.Parse(RunId),
            Flow = new LegacyPayloadFlow(),
            Trigger = new Trigger("manual", "Manual", null),
        };

        // Act
        var json = JsonConvert.SerializeObject(ctx, typeof(IExecutionContext), HangfireArgumentSettings);

        // Assert
        Assert.DoesNotContain("Manifest", json, StringComparison.Ordinal);
        Assert.Contains("\"Key\":\"manual\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TriggerContext_SerialisedBySystemTextJson_StillCarriesTheFlow()
    {
        // Arrange — [IgnoreDataMember] targets Newtonsoft only; System.Text.Json ignores it.
        var ctx = new TriggerContext
        {
            RunId = Guid.Parse(RunId),
            Flow = new LegacyPayloadFlow(),
            Trigger = new Trigger("manual", "Manual", null),
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize(ctx);

        // Assert
        Assert.Contains("\"Flow\":", json, StringComparison.Ordinal);
    }
}

/// <summary>Flow type named by the legacy payload fixture's <c>$type</c>.</summary>
public sealed class LegacyPayloadFlow : IFlowDefinition
{
    /// <inheritdoc/>
    public Guid Id { get; set; } = Guid.Parse("0b3c7d1e-9a4f-4e2b-8c6d-5f1a2b3c4d5e");

    /// <inheritdoc/>
    public string Version { get; set; } = "1.0";

    /// <inheritdoc/>
    public FlowManifest Manifest { get; set; } = new();
}
