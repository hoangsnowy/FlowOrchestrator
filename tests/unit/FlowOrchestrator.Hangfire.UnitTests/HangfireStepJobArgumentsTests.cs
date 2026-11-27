using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using NSubstitute;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// Covers <see cref="HangfireStepJobArguments"/>: the dispatching side hands Hangfire inputs without
/// <see cref="JsonElement"/> values (Newtonsoft.Json destroys them), and the worker side repairs the
/// destroyed inputs that jobs enqueued by 1.34.7 or earlier still carry.
/// </summary>
public sealed class HangfireStepJobArgumentsTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void PrepareForEnqueue_InputsWithoutJsonElements_ReturnsTheSameInstance()
    {
        // Arrange
        var step = new StepInstance("track", "CarrierStatus")
        {
            Inputs = new Dictionary<string, object?> { ["shipmentId"] = "S1", ["pollIntervalSeconds"] = 30, ["nested"] = new Dictionary<string, object?> { ["a"] = 1 } },
        };

        // Act
        var prepared = HangfireStepJobArguments.PrepareForEnqueue(step);

        // Assert
        Assert.Same(step, prepared);
    }

    [Fact]
    public void PrepareForEnqueue_JsonElementInputs_CarriesThemAsOneJsonStringAndLeavesTheOriginalAlone()
    {
        // Arrange
        var original = new Dictionary<string, object?>
        {
            ["shipmentId"] = Json("\"S1\""),
            ["count"] = Json("3"),
            ["address"] = Json("""{"city":"Hanoi","lines":["a",2]}"""),
            ["plain"] = "kept",
            ["attempt"] = 2,
        };
        var step = new StepInstance("track", "CarrierStatus")
        {
            RunId = Guid.NewGuid(),
            Index = 2,
            ScheduledTime = DateTimeOffset.UnixEpoch,
            Inputs = original,
        };

        // Act
        var prepared = HangfireStepJobArguments.PrepareForEnqueue(step);

        // Assert — the job argument holds only strings and primitives.
        Assert.NotSame(step, prepared);
        Assert.Same(original, step.Inputs);
        Assert.IsType<JsonElement>(step.Inputs["shipmentId"]);
        Assert.Equal(step.RunId, prepared.RunId);
        Assert.Equal("track", prepared.Key);
        Assert.Equal("CarrierStatus", prepared.Type);
        Assert.Equal(2, prepared.Index);
        Assert.Equal(DateTimeOffset.UnixEpoch, prepared.ScheduledTime);
        Assert.Equal(["plain", "attempt", HangfireStepJobArguments.EncodedInputsKey], prepared.Inputs.Keys);
        Assert.Equal("kept", prepared.Inputs["plain"]);
        Assert.Equal(2, prepared.Inputs["attempt"]);
        var encoded = Assert.IsType<string>(prepared.Inputs[HangfireStepJobArguments.EncodedInputsKey]);
        Assert.Equal("""{"shipmentId":"S1","count":3,"address":{"city":"Hanoi","lines":["a",2]}}""", encoded);
    }

    [Fact]
    public void RestoreOnWorker_EncodedInputs_GetBackTheExactJsonElementValues()
    {
        // Arrange — a date-like string, a big decimal and a nested object, all of which must come back verbatim.
        var step = new StepInstance("track", "CarrierStatus")
        {
            Inputs = new Dictionary<string, object?>
            {
                ["__pollStartedAtUtc"] = Json("\"2026-11-27T10:00:00.1234567+00:00\""),
                ["amount"] = Json("12345678901234567890.123"),
                ["address"] = Json("""{"city":"Hanoi"}"""),
                ["__pollAttempt"] = 1,
            },
        };
        var job = HangfireStepJobArguments.PrepareForEnqueue(step);

        // Act
        var restored = HangfireStepJobArguments.RestoreOnWorker(new FlowOrchestrator.Core.Execution.ExecutionContext(), job, Flow());

        // Assert
        Assert.Equal(3, restored);
        Assert.False(job.Inputs.ContainsKey(HangfireStepJobArguments.EncodedInputsKey));
        Assert.Equal("2026-11-27T10:00:00.1234567+00:00", Assert.IsType<JsonElement>(job.Inputs["__pollStartedAtUtc"]).GetString());
        Assert.Equal("12345678901234567890.123", Assert.IsType<JsonElement>(job.Inputs["amount"]).GetRawText());
        Assert.Equal("""{"city":"Hanoi"}""", Assert.IsType<JsonElement>(job.Inputs["address"]).GetRawText());
        Assert.Equal(1, job.Inputs["__pollAttempt"]);
    }

    [Fact]
    public async Task ScheduleStepAsync_PendingStepWithJsonElementInputs_HandsHangfireThemAsAJsonString()
    {
        // Arrange
        var client = Substitute.For<IBackgroundJobClient>();
        Job? captured = null;
        client.Create(Arg.Do<Job>(j => captured = j), Arg.Any<IState>()).Returns("job-1");
        var sut = new HangfireStepDispatcher(client);
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var step = new StepInstance("track", "CarrierStatus") { Inputs = new Dictionary<string, object?> { ["shipmentId"] = Json("\"S1\"") } };

        // Act
        await sut.ScheduleStepAsync(new FlowOrchestrator.Core.Execution.ExecutionContext(), flow, step, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert
        Assert.NotNull(captured);
        var jobStep = Assert.IsAssignableFrom<IStepInstance>(captured.Args[2]);
        Assert.False(jobStep.Inputs.ContainsKey("shipmentId"));
        Assert.Equal("""{"shipmentId":"S1"}""", jobStep.Inputs[HangfireStepJobArguments.EncodedInputsKey]);
        Assert.IsType<JsonElement>(step.Inputs["shipmentId"]);
    }

    [Fact]
    public void RestoreOnWorker_DestroyedInputs_RestoresDeclaredOnesFromTheManifestAndDropsTheRest()
    {
        // Arrange — the argument shape of a 1.34.7 poll re-check job after Newtonsoft deserialised it.
        var undefined = default(JsonElement);
        var step = new StepInstance("track", "CarrierStatus")
        {
            TriggerData = undefined,
            Inputs = new Dictionary<string, object?>
            {
                ["shipmentId"] = undefined,
                ["PollIntervalSeconds"] = undefined,
                ["__pollAttempt"] = undefined,
                ["__loopItem"] = undefined,
                ["kept"] = "plain value",
            },
        };
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { TriggerData = undefined };

        // Act
        var repaired = HangfireStepJobArguments.RestoreOnWorker(ctx, step, Flow());

        // Assert
        Assert.Equal(4, repaired);
        Assert.Null(ctx.TriggerData);
        Assert.Null(step.TriggerData);
        Assert.Equal("@triggerBody()?.shipmentId", step.Inputs["shipmentId"]);
        Assert.Equal(30, step.Inputs["pollIntervalSeconds"]);
        Assert.False(step.Inputs.ContainsKey("PollIntervalSeconds"));
        Assert.False(step.Inputs.ContainsKey("__pollAttempt"));
        Assert.False(step.Inputs.ContainsKey("__loopItem"));
        Assert.Equal("plain value", step.Inputs["kept"]);
    }

    [Fact]
    public void RestoreOnWorker_IntactInputs_LeavesTheStepUntouched()
    {
        // Arrange
        var inputs = new Dictionary<string, object?> { ["shipmentId"] = "S1", ["__pollAttempt"] = 2L };
        var triggerData = Json("""{"shipmentId":"S1"}""");
        var step = new StepInstance("track", "CarrierStatus") { Inputs = inputs, TriggerData = triggerData };

        // Act
        var repaired = HangfireStepJobArguments.RestoreOnWorker(new FlowOrchestrator.Core.Execution.ExecutionContext(), step, Flow());

        // Assert
        Assert.Equal(0, repaired);
        Assert.Same(inputs, step.Inputs);
        Assert.Equal(triggerData, step.TriggerData);
    }

    [Fact]
    public void IsDestroyed_FindsAnUndefinedElementAtAnyDepth()
    {
        // Arrange
        var nested = new Dictionary<string, object?> { ["a"] = new List<object?> { "x", default(JsonElement) } };

        // Act
        var destroyed = HangfireStepJobArguments.IsDestroyed(nested);
        var intact = HangfireStepJobArguments.IsDestroyed(new Dictionary<string, object?> { ["a"] = Json("1"), ["b"] = "s" });

        // Assert
        Assert.True(destroyed);
        Assert.False(intact);
    }

    private static IFlowDefinition Flow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["track"] = new StepMetadata
                {
                    Type = "CarrierStatus",
                    Inputs = new Dictionary<string, object?>
                    {
                        ["shipmentId"] = "@triggerBody()?.shipmentId",
                        ["pollIntervalSeconds"] = 30,
                    },
                },
            },
        });
        return flow;
    }
}
