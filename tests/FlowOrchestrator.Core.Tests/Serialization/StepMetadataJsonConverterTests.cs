using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Serialization;

public class StepMetadataJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserialize_PlainStep_ReturnsStepMetadata()
    {
        var json = """
        {
            "type": "LogMessage",
            "runAfter": { "step0": ["Succeeded"] },
            "inputs": { "message": "hello" }
        }
        """;

        var result = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        result.Should().NotBeNull();
        result.Should().BeOfType<StepMetadata>().And.NotBeOfType<LoopStepMetadata>();
        result!.Type.Should().Be("LogMessage");
        result.RunAfter.Should().ContainKey("step0");
        result.RunAfter["step0"].Should().Contain(StepStatus.Succeeded);
        result.Inputs.Should().ContainKey("message");
    }

    [Fact]
    public void Deserialize_ForEachStep_ReturnsLoopStepMetadata()
    {
        var json = """
        {
            "type": "ForEach",
            "runAfter": {},
            "inputs": {},
            "forEach": [1, 2, 3],
            "concurrencyLimit": 5,
            "steps": {
                "child1": { "type": "Process", "runAfter": {}, "inputs": {} }
            }
        }
        """;

        var result = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        result.Should().BeOfType<LoopStepMetadata>();
        var loop = (LoopStepMetadata)result!;
        loop.Type.Should().Be("ForEach");
        loop.ConcurrencyLimit.Should().Be(5);
        loop.Steps.Should().ContainKey("child1");
        loop.Steps["child1"].Type.Should().Be("Process");
    }

    [Fact]
    public void Serialize_PlainStep_ProducesCorrectJson()
    {
        var step = new StepMetadata
        {
            Type = "LogMessage",
            RunAfter = new RunAfterCollection { ["step0"] = new[] { StepStatus.Succeeded } },
            Inputs = new Dictionary<string, object?> { ["message"] = "hello" }
        };

        var json = JsonSerializer.Serialize(step, Options);

        json.Should().Contain("\"type\":\"LogMessage\"");
        json.Should().Contain("\"runAfter\"");
        json.Should().Contain("\"inputs\"");
    }

    [Fact]
    public void RoundTrip_PlainStep_PreservesData()
    {
        var original = new StepMetadata
        {
            Type = "QueryDatabase",
            RunAfter = new RunAfterCollection { ["prev"] = new[] { StepStatus.Succeeded, StepStatus.Failed } },
            Inputs = new Dictionary<string, object?> { ["sql"] = "SELECT 1", ["timeout"] = 30 }
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        deserialized.Should().NotBeNull();
        deserialized!.Type.Should().Be(original.Type);
        deserialized.RunAfter.Should().ContainKey("prev");
        deserialized.RunAfter["prev"].Should().BeEquivalentTo(new[] { StepStatus.Succeeded, StepStatus.Failed });
    }

    [Fact]
    public void RoundTrip_LoopStep_PreservesNestedSteps()
    {
        var original = new LoopStepMetadata
        {
            Type = "ForEach",
            ConcurrencyLimit = 3,
            Steps = new StepCollection
            {
                ["inner"] = new StepMetadata
                {
                    Type = "Process",
                    Inputs = new Dictionary<string, object?> { ["key"] = "value" }
                }
            }
        };

        var json = JsonSerializer.Serialize<StepMetadata>(original, Options);
        var deserialized = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        deserialized.Should().BeOfType<LoopStepMetadata>();
        var loop = (LoopStepMetadata)deserialized!;
        loop.ConcurrencyLimit.Should().Be(3);
        loop.Steps.Should().ContainKey("inner");
        loop.Steps["inner"].Type.Should().Be("Process");
    }

    [Fact]
    public void Deserialize_StepCollection_PolymorphicSteps()
    {
        var json = """
        {
            "plain": { "type": "LogMessage", "runAfter": {}, "inputs": {} },
            "loop": { "type": "ForEach", "runAfter": {}, "inputs": {}, "forEach": null, "concurrencyLimit": 2, "steps": {} }
        }
        """;

        var result = JsonSerializer.Deserialize<StepCollection>(json, Options);

        result.Should().NotBeNull();
        result!["plain"].Should().BeOfType<StepMetadata>();
        result["loop"].Should().BeOfType<LoopStepMetadata>();
        ((LoopStepMetadata)result["loop"]).ConcurrencyLimit.Should().Be(2);
    }

    [Fact]
    public void Deserialize_MissingType_DefaultsToNull()
    {
        var json = """{ "runAfter": {}, "inputs": {} }""";

        var result = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        result.Should().NotBeNull();
        result!.Type.Should().BeNull();
    }
}
