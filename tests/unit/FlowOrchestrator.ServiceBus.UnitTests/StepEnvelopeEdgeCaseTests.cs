using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.ServiceBus;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Edge-case coverage for <see cref="StepEnvelope.From"/> and
/// <see cref="StepEnvelope.ToStepInstance"/>: null inputs, deeply nested objects,
/// JsonElement value-kind handling, and trigger-header case insensitivity. The wire
/// envelope is the cross-process boundary, so any silent data loss here corrupts
/// every step that crosses Service Bus.
/// </summary>
public class StepEnvelopeEdgeCaseTests
{
    private static IFlowDefinition MakeFlow()
    {
        var f = Substitute.For<IFlowDefinition>();
        f.Id.Returns(Guid.NewGuid());
        return f;
    }

    private static FlowOrchestrator.Core.Execution.ExecutionContext MakeCtx() => new()
    {
        RunId = Guid.NewGuid(),
    };

    [Fact]
    public void From_NullInputValue_RoundTripsAsJsonNull()
    {
        // Arrange
        var ctx = MakeCtx();
        var step = new StepInstance("s", "T") { RunId = ctx.RunId };
        step.Inputs["maybe"] = null;

        // Act
        var envelope = StepEnvelope.From(ctx, MakeFlow().Id, step);
        var json = JsonSerializer.Serialize(envelope);
        var restored = JsonSerializer.Deserialize<StepEnvelope>(json)!;
        var restoredStep = restored.ToStepInstance();

        // Assert — the input survives as a Null entry; ToStepInstance maps Null → null reference.
        Assert.True(restored.Inputs!.ContainsKey("maybe"));
        Assert.Equal(JsonValueKind.Null, restored.Inputs["maybe"].ValueKind);
        Assert.Null(restoredStep.Inputs["maybe"]);
    }

    [Fact]
    public void From_DeeplyNestedObjectInput_PreservesShape()
    {
        // Arrange
        var ctx = MakeCtx();
        var step = new StepInstance("s", "T") { RunId = ctx.RunId };
        step.Inputs["nested"] = new
        {
            a = new
            {
                b = new
                {
                    c = new { d = "deep" },
                },
            },
        };

        // Act
        var envelope = StepEnvelope.From(ctx, MakeFlow().Id, step);
        var json = JsonSerializer.Serialize(envelope);
        var restored = JsonSerializer.Deserialize<StepEnvelope>(json)!;

        // Assert — drill through the cloned JsonElement to confirm the leaf survives.
        var nested = restored.Inputs!["nested"];
        Assert.Equal(JsonValueKind.Object, nested.ValueKind);
        var a = nested.GetProperty("a");
        var b = a.GetProperty("b");
        var c = b.GetProperty("c");
        var d = c.GetProperty("d");
        Assert.Equal("deep", d.GetString());
    }

    [Fact]
    public void From_ArrayInput_PreservesElements()
    {
        // Arrange
        var ctx = MakeCtx();
        var step = new StepInstance("s", "T") { RunId = ctx.RunId };
        step.Inputs["items"] = new[] { 1, 2, 3 };

        // Act
        var envelope = StepEnvelope.From(ctx, MakeFlow().Id, step);
        var restored = JsonSerializer.Deserialize<StepEnvelope>(JsonSerializer.Serialize(envelope))!;

        // Assert
        var arr = restored.Inputs!["items"];
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(3, arr.GetArrayLength());
        Assert.Equal(1, arr[0].GetInt32());
        Assert.Equal(2, arr[1].GetInt32());
        Assert.Equal(3, arr[2].GetInt32());
    }

    [Fact]
    public void ToExecutionContext_HeaderLookup_IsCaseInsensitive()
    {
        // Arrange — ToExecutionContext uses OrdinalIgnoreCase for trigger headers, matching
        // ASP.NET Core conventions. Lose this and webhook headers stop resolving cross-process.
        var envelope = new StepEnvelope
        {
            RunId = Guid.NewGuid(),
            TriggerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Foo"] = "bar",
            },
        };

        // Act
        var ctx = envelope.ToExecutionContext();

        // Assert
        Assert.Equal("bar", ctx.TriggerHeaders!["x-foo"]);
        Assert.Equal("bar", ctx.TriggerHeaders["X-FOO"]);
    }

    [Fact]
    public void ToStepInstance_InputWithJsonValueKindUndefined_TreatsAsNull()
    {
        // Arrange — fabricate an envelope with an Undefined-valued input. This can occur
        // if a producer hand-builds the dictionary or if a partial JSON deserialisation
        // leaves a field at default(JsonElement). Treating it as null (rather than calling
        // JsonElement.Clone() which throws) prevents an abandon-redeliver loop on poison
        // messages with malformed inputs.
        var envelope = new StepEnvelope
        {
            RunId = Guid.NewGuid(),
            StepKey = "k",
            StepType = "T",
            Inputs = new Dictionary<string, JsonElement>
            {
                ["empty"] = default, // ValueKind == Undefined
            },
        };

        // Act
        var step = envelope.ToStepInstance();

        // Assert — the key is present in the inputs dict with a null value, identical to
        // the JsonValueKind.Null path. Handlers that read the input via TryGetValue or
        // null-coalesce see the same shape regardless of producer-side encoding.
        Assert.True(step.Inputs.ContainsKey("empty"));
        Assert.Null(step.Inputs["empty"]);
    }

    [Fact]
    public void From_WithNullTriggerData_ProducesNullEnvelopeField()
    {
        // Arrange
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext
        {
            RunId = Guid.NewGuid(),
            TriggerData = null,
        };
        var step = new StepInstance("s", "T") { RunId = ctx.RunId };

        // Act
        var envelope = StepEnvelope.From(ctx, Guid.NewGuid(), step);

        // Assert
        Assert.Null(envelope.TriggerData);
    }
}
