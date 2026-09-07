using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Serialization;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// End-to-end coverage for issues #176 and #177 through <see cref="DefaultStepExecutor"/>, the
/// single choke point every step execution passes through regardless of which dispatch path
/// (fan-out, DAG continuation, signal resume, retry, crash recovery) enqueued it.
/// </summary>
public class DefaultStepExecutorLoopScopeTests
{
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IOutputsRepository _outputs = Substitute.For<IOutputsRepository>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();

    /// <summary>The handler input POCO exactly as issue #176 declares it.</summary>
    private sealed class OpenCameraInput
    {
        [JsonPropertyName("__loopItem")]
        public object? LoopItem { get; set; }

        [JsonPropertyName("__loopIndex")]
        public int LoopIndex { get; set; }

        public string OrderNo { get; set; } = string.Empty;

        public string Location { get; set; } = string.Empty;
    }

    private static StepCollection BuildSteps() => new()
    {
        ["scan_start"] = new StepMetadata { Type = "ScanStart" },
        ["scan_process"] = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = "@triggerBody()?.Steps",
            ConcurrencyLimit = 1,
            Steps = new StepCollection
            {
                ["wait_robot_goto"] = new StepMetadata { Type = "WaitForSignal" },
                ["open_camera"] = new StepMetadata
                {
                    Type = "OpenCamera",
                    RunAfter = new RunAfterCollection { { "wait_robot_goto", [StepStatus.Succeeded] } },
                    Inputs = new Dictionary<string, object?>
                    {
                        ["OrderNo"] = "@triggerBody()?.OrderNo",
                        ["Location"] = "@steps('wait_robot_goto').output.Location"
                    }
                }
            }
        }
    };

    private static IFlowDefinition CreateFlow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    /// <summary>
    /// Captures the inputs the handler actually observed, so the assertions read the same values a
    /// real handler would.
    /// </summary>
    private IStepHandlerMetadata CaptureHandler(string type, Action<IDictionary<string, object?>> capture)
    {
        var handler = Substitute.For<IStepHandlerMetadata>();
        handler.Type.Returns(type);
        handler
            .ExecuteAsync(
                Arg.Any<IServiceProvider>(),
                Arg.Any<IExecutionContext>(),
                Arg.Any<IFlowDefinition>(),
                Arg.Do<IStepInstance>(
                    instance => capture(new Dictionary<string, object?>(instance.Inputs, StringComparer.Ordinal))))
            .Returns(new ValueTask<IStepResult>(new StepResult { Key = "captured", Status = StepStatus.Succeeded }));
        return handler;
    }

    [Fact]
    public async Task DependentLoopChild_SeesLoopItemAndIndexAndResolvedSiblingOutput()
    {
        // Arrange — the continuation dispatches open_camera with the loop's TEMPLATE inputs, and the
        // sibling's output is persisted camelCase while the manifest asks for ".Location".
        var flow = CreateFlow(BuildSteps());
        var runId = Guid.NewGuid();
        var trigger = JsonSerializer.Deserialize<JsonElement>(
            "{\"OrderNo\":\"ORD-1\",\"Steps\":[{\"code\":\"A\"},{\"code\":\"B\"}]}");
        var ctx = new Core.Execution.ExecutionContext { RunId = runId, TriggerData = trigger };

        _outputs.GetStepOutputAsync(runId, "scan_process.1.wait_robot_goto")
            .Returns(new ValueTask<object?>(JsonSerializer.Deserialize<JsonElement>("{\"location\":\"BIN-42\"}")));

        IDictionary<string, object?>? observed = null;
        var executor = new DefaultStepExecutor(
            [CaptureHandler("OpenCamera", inputs => observed = inputs)],
            _serviceProvider,
            _outputs,
            _runStore);

        var step = new StepInstance("scan_process.1.open_camera", "OpenCamera")
        {
            RunId = runId,
            TriggerData = trigger,
            Inputs = new Dictionary<string, object?>
            {
                ["OrderNo"] = "@triggerBody()?.OrderNo",
                ["Location"] = "@steps('wait_robot_goto').output.Location"
            }
        };

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.NotNull(observed);
        Assert.Equal(1, observed!["__loopIndex"]);
        var item = Assert.IsType<Dictionary<string, object?>>(observed["__loopItem"]);
        Assert.Equal("B", item["code"]);

        // And the binder the real handler goes through maps them onto the typed POCO.
        var typed = JsonValueConversion.Deserialize<OpenCameraInput>(observed);
        Assert.NotNull(typed);
        Assert.Equal(1, typed!.LoopIndex);
        Assert.Equal("ORD-1", typed.OrderNo);
        Assert.Equal("BIN-42", typed.Location);
        Assert.NotNull(typed.LoopItem);
    }

    [Fact]
    public async Task LoopScopeInputs_ArePersistedWithTheStepInput()
    {
        // Arrange — the dashboard reads the saved input record, so it must show the iteration too.
        var flow = CreateFlow(BuildSteps());
        var runId = Guid.NewGuid();
        var trigger = JsonSerializer.Deserialize<JsonElement>(
            "{\"OrderNo\":\"ORD-1\",\"Steps\":[{\"code\":\"A\"},{\"code\":\"B\"}]}");
        var ctx = new Core.Execution.ExecutionContext { RunId = runId, TriggerData = trigger };

        IDictionary<string, object?>? saved = null;
        await _outputs.SaveStepInputAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Do<IStepInstance>(
                s => saved = new Dictionary<string, object?>(s.Inputs, StringComparer.Ordinal)));

        var executor = new DefaultStepExecutor(
            [CaptureHandler("WaitForSignal", _ => { })],
            _serviceProvider,
            _outputs,
            _runStore);

        var step = new StepInstance("scan_process.0.wait_robot_goto", "WaitForSignal")
        {
            RunId = runId,
            TriggerData = trigger
        };

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.NotNull(saved);
        Assert.Equal(0, saved!["__loopIndex"]);
        var item = Assert.IsType<Dictionary<string, object?>>(saved["__loopItem"]);
        Assert.Equal("A", item["code"]);
    }

    [Fact]
    public async Task TopLevelStep_GetsNoLoopKeys()
    {
        // Arrange
        var flow = CreateFlow(BuildSteps());
        var runId = Guid.NewGuid();
        var trigger = JsonSerializer.Deserialize<JsonElement>(
            "{\"OrderNo\":\"ORD-1\",\"Steps\":[{\"code\":\"A\"}]}");
        var ctx = new Core.Execution.ExecutionContext { RunId = runId, TriggerData = trigger };

        IDictionary<string, object?>? observed = null;
        var executor = new DefaultStepExecutor(
            [CaptureHandler("ScanStart", inputs => observed = inputs)],
            _serviceProvider,
            _outputs,
            _runStore);

        var step = new StepInstance("scan_start", "ScanStart") { RunId = runId, TriggerData = trigger };

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.NotNull(observed);
        Assert.False(observed!.ContainsKey("__loopItem"));
        Assert.False(observed.ContainsKey("__loopIndex"));
        Assert.Equal(0, step.Index);
    }

    [Theory]
    [InlineData("scan_process.0.wait_robot_goto", "WaitForSignal", 0)]
    [InlineData("scan_process.1.open_camera", "OpenCamera", 1)]
    public async Task LoopChild_GetsItsIterationOnStepInstanceIndex(
        string runtimeStepKey, string stepType, int expectedIndex)
    {
        // Arrange — IStepInstance.Index documents itself as the enclosing loop's iteration index,
        // but no dispatch site assigned it, so every iteration reported 0.
        var flow = CreateFlow(BuildSteps());
        var runId = Guid.NewGuid();
        var trigger = JsonSerializer.Deserialize<JsonElement>(
            "{\"OrderNo\":\"ORD-1\",\"Steps\":[{\"code\":\"A\"},{\"code\":\"B\"}]}");
        var ctx = new Core.Execution.ExecutionContext { RunId = runId, TriggerData = trigger };

        var observedIndex = -99;
        var handler = Substitute.For<IStepHandlerMetadata>();
        handler.Type.Returns(stepType);
        handler
            .ExecuteAsync(
                Arg.Any<IServiceProvider>(),
                Arg.Any<IExecutionContext>(),
                Arg.Any<IFlowDefinition>(),
                Arg.Do<IStepInstance>(instance => observedIndex = instance.Index))
            .Returns(new ValueTask<IStepResult>(new StepResult { Key = "captured", Status = StepStatus.Succeeded }));

        var executor = new DefaultStepExecutor([handler], _serviceProvider, _outputs, _runStore);
        var step = new StepInstance(runtimeStepKey, stepType) { RunId = runId, TriggerData = trigger };

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(expectedIndex, observedIndex);
    }
}
