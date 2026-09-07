using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Expressions;

/// <summary>
/// Regression coverage for issue #177 — a manifest expression written in the CLR's PascalCase
/// (<c>@steps('scan_vision').output.Location</c>) resolved to <see langword="null"/> because the
/// step output had been persisted with web serializer conventions (<c>{"location":…}</c>) and the
/// path walker matched property names case-sensitively.
/// </summary>
public class StepOutputPascalCasePathTests
{
    private readonly IOutputsRepository _outputs = Substitute.For<IOutputsRepository>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly Guid _runId = Guid.NewGuid();

    private static readonly StepCollection _steps = new()
    {
        ["scan_vision"] = new StepMetadata { Type = "ScanVision" },
        ["scan_done"] = new StepMetadata
        {
            Type = "ScanDone",
            RunAfter = new RunAfterCollection { { "scan_vision", [StepStatus.Succeeded] } }
        }
    };

    /// <summary>The output POCO exactly as issue #177 declares it — PascalCase CLR properties.</summary>
    private sealed class ScanVisionOutput
    {
        public string Location { get; set; } = string.Empty;
    }

    private StepOutputResolver CreateResolver() => new(_outputs, _runStore, _runId, _steps);

    private void StubOutput(object? value) =>
        _outputs.GetStepOutputAsync(_runId, "scan_vision").Returns(new ValueTask<object?>(value));

    [Fact]
    public async Task PascalCasePath_ResolvesAgainstCamelCasePersistedOutput()
    {
        // Arrange — this is what SqlOutputsRepository/PostgreSqlOutputsRepository write to storage:
        // the handler's ScanVisionOutput serialized with JsonSerializerDefaults.Web.
        StubOutput(JsonSerializer.Deserialize<JsonElement>("{\"location\":\"BIN-42\"}"));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('scan_vision').output.Location");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("BIN-42", element.GetString());
    }

    [Fact]
    public async Task PascalCasePath_ResolvesAgainstClrObjectOutput()
    {
        // Arrange — an in-memory store hands back the CLR object itself; the resolver serializes it
        // with web conventions, so the same PascalCase/camelCase mismatch has to be tolerated.
        StubOutput(new ScanVisionOutput { Location = "BIN-7" });
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('scan_vision').output.Location");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("BIN-7", element.GetString());
    }

    [Fact]
    public async Task ExactCaseMatch_StillWinsOverCaseInsensitiveSibling()
    {
        // Arrange — a payload carrying both spellings must resolve deterministically to the one
        // the expression asked for, not to whichever the object happens to enumerate first.
        StubOutput(JsonSerializer.Deserialize<JsonElement>("{\"location\":\"lower\",\"Location\":\"upper\"}"));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('scan_vision').output.Location");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("upper", element.GetString());
    }

    [Fact]
    public async Task CamelCasePath_KeepsResolvingAgainstCamelCaseOutput()
    {
        // Arrange
        StubOutput(JsonSerializer.Deserialize<JsonElement>("{\"location\":\"BIN-1\"}"));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('scan_vision').output.location");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("BIN-1", element.GetString());
    }

    [Fact]
    public async Task NestedPascalCasePath_ResolvesThroughEverySegment()
    {
        // Arrange
        StubOutput(JsonSerializer.Deserialize<JsonElement>(
            "{\"camera\":{\"lastFrame\":{\"binCode\":\"B-9\"}}}"));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('scan_vision').output.Camera.LastFrame.BinCode");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("B-9", element.GetString());
    }

    [Fact]
    public async Task GenuinelyMissingProperty_StillResolvesToNull()
    {
        // Arrange — the relaxed match must not start inventing values for absent properties.
        StubOutput(JsonSerializer.Deserialize<JsonElement>("{\"location\":\"BIN-1\"}"));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('scan_vision').output.Warehouse");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void PascalCaseTriggerBodyPath_ResolvesAgainstCamelCasePayload()
    {
        // Arrange — same walker backs @triggerBody(); a webhook posting camelCase JSON must bind to
        // a manifest written in PascalCase.
        var payload = JsonSerializer.Deserialize<JsonElement>("{\"orderNo\":\"ORD-3\"}");

        // Act
        var resolved = TriggerExpressionResolver.TryResolveTriggerBodyExpression(
            "@triggerBody()?.OrderNo", payload, out var value);

        // Assert
        Assert.True(resolved);
        var element = Assert.IsType<JsonElement>(value);
        Assert.Equal("ORD-3", element.GetString());
    }
}
