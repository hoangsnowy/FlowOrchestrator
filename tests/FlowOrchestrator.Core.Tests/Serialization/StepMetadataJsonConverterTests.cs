using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Tests.Serialization;

public class StepMetadataJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserialize_PlainStep_ReturnsStepMetadata()
    {
        // Arrange
        var json = """
        {
            "type": "LogMessage",
            "runAfter": { "step0": ["Succeeded"] },
            "inputs": { "message": "hello" }
        }
        """;

        // Act
        var result = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<StepMetadata>(result);
        Assert.IsNotType<LoopStepMetadata>(result);
        Assert.Equal("LogMessage", result!.Type);
        Assert.True(result.RunAfter.ContainsKey("step0"));
        Assert.Contains(StepStatus.Succeeded, result.RunAfter["step0"]);
        Assert.True(result.Inputs.ContainsKey("message"));
    }

    [Fact]
    public void Deserialize_ForEachStep_ReturnsLoopStepMetadata()
    {
        // Arrange
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

        // Act
        var result = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        // Assert
        var loop = Assert.IsType<LoopStepMetadata>(result);
        Assert.Equal("ForEach", loop.Type);
        Assert.Equal(5, loop.ConcurrencyLimit);
        Assert.True(loop.Steps.ContainsKey("child1"));
        Assert.Equal("Process", loop.Steps["child1"].Type);
    }

    [Fact]
    public void Serialize_PlainStep_ProducesCorrectJson()
    {
        // Arrange
        var step = new StepMetadata
        {
            Type = "LogMessage",
            RunAfter = new RunAfterCollection { ["step0"] = new[] { StepStatus.Succeeded } },
            Inputs = new Dictionary<string, object?> { ["message"] = "hello" }
        };

        // Act
        var json = JsonSerializer.Serialize(step, Options);

        // Assert
        Assert.Contains("\"type\":\"LogMessage\"", json);
        Assert.Contains("\"runAfter\"", json);
        Assert.Contains("\"inputs\"", json);
    }

    [Fact]
    public void RoundTrip_PlainStep_PreservesData()
    {
        // Arrange
        var original = new StepMetadata
        {
            Type = "QueryDatabase",
            RunAfter = new RunAfterCollection { ["prev"] = new[] { StepStatus.Succeeded, StepStatus.Failed } },
            Inputs = new Dictionary<string, object?> { ["sql"] = "SELECT 1", ["timeout"] = 30 }
        };

        // Act
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Type, deserialized!.Type);
        Assert.True(deserialized.RunAfter.ContainsKey("prev"));
        Assert.Equal(new[] { StepStatus.Succeeded, StepStatus.Failed }, deserialized.RunAfter["prev"]);
    }

    [Fact]
    public void RoundTrip_LoopStep_PreservesNestedSteps()
    {
        // Arrange
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

        // Act
        var json = JsonSerializer.Serialize<StepMetadata>(original, Options);
        var deserialized = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        // Assert
        var loop = Assert.IsType<LoopStepMetadata>(deserialized);
        Assert.Equal(3, loop.ConcurrencyLimit);
        Assert.True(loop.Steps.ContainsKey("inner"));
        Assert.Equal("Process", loop.Steps["inner"].Type);
    }

    [Fact]
    public void Deserialize_StepCollection_PolymorphicSteps()
    {
        // Arrange
        var json = """
        {
            "plain": { "type": "LogMessage", "runAfter": {}, "inputs": {} },
            "loop": { "type": "ForEach", "runAfter": {}, "inputs": {}, "forEach": null, "concurrencyLimit": 2, "steps": {} }
        }
        """;

        // Act
        var result = JsonSerializer.Deserialize<StepCollection>(json, Options);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<StepMetadata>(result!["plain"]);
        var loop = Assert.IsType<LoopStepMetadata>(result["loop"]);
        Assert.Equal(2, loop.ConcurrencyLimit);
    }

    [Fact]
    public void Deserialize_MissingType_DefaultsToNull()
    {
        // Arrange
        var json = """{ "runAfter": {}, "inputs": {} }""";

        // Act
        var result = JsonSerializer.Deserialize<StepMetadata>(json, Options);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result!.Type);
    }
}
