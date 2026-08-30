using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Expressions;

public class StepOutputResolverTests
{
    private readonly IOutputsRepository _outputs = Substitute.For<IOutputsRepository>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly Guid _runId = Guid.NewGuid();

    private static readonly StepCollection _defaultSteps = new()
    {
        ["fetch_orders"] = new StepMetadata { Type = "Fetch" },
        ["submit"] = new StepMetadata { Type = "Submit" }
    };

    // A flow whose ForEach loop "scan" wraps two sibling child steps. The child steps
    // reference each other's output by bare key, exactly as reported in issue #166.
    private static readonly StepCollection _loopSteps = new()
    {
        ["fetch_orders"] = new StepMetadata { Type = "Fetch" },
        ["scan"] = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = "@triggerBody()?.items",
            Steps = new StepCollection
            {
                ["wait_robot_goto"] = new StepMetadata { Type = "WaitForSignal" },
                ["open_camera"] = new StepMetadata
                {
                    Type = "OpenCamera",
                    RunAfter = new RunAfterCollection { { "wait_robot_goto", [StepStatus.Succeeded] } }
                }
            }
        }
    };

    private StepOutputResolver CreateResolver(StepCollection? steps = null) =>
        new(_outputs, _runStore, _runId, steps ?? _defaultSteps);

    private StepOutputResolver CreateResolver(StepCollection steps, string currentStepKey) =>
        new(_outputs, _runStore, _runId, steps, currentStepKey);

    private static JsonElement Json(string raw) =>
        JsonSerializer.Deserialize<JsonElement>(raw);

    // ── Output resolution ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResolvesTopLevelFieldFromPriorStepOutput()
    {
        // Arrange
        var output = Json("{\"orderId\":\"ORD-1\",\"total\":99}");
        _outputs.GetStepOutputAsync(_runId, "fetch_orders")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('fetch_orders').output.orderId");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("ORD-1", element.GetString());
    }

    [Fact]
    public async Task ResolvesNestedFieldViaDoNotation()
    {
        // Arrange
        var output = Json("{\"customer\":{\"address\":{\"city\":\"NYC\"}}}");
        _outputs.GetStepOutputAsync(_runId, "fetch_orders")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('fetch_orders').output.customer.address.city");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("NYC", element.GetString());
    }

    [Fact]
    public async Task ResolvesArrayElementByIndex()
    {
        // Arrange
        var output = Json("{\"items\":[\"alpha\",\"beta\"]}");
        _outputs.GetStepOutputAsync(_runId, "fetch_orders")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('fetch_orders').output.items[0]");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("alpha", element.GetString());
    }

    [Fact]
    public async Task ResolvesCombinedArrayIndexAndNestedField()
    {
        // Arrange
        var output = Json("{\"items\":[{\"name\":\"Widget\"},{\"name\":\"Gadget\"}]}");
        _outputs.GetStepOutputAsync(_runId, "fetch_orders")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('fetch_orders').output.items[1].name");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("Gadget", element.GetString());
    }

    [Fact]
    public async Task ReturnsNullForMissingFieldOnExistingStep()
    {
        // Arrange
        var output = Json("{\"orderId\":\"ORD-1\"}");
        _outputs.GetStepOutputAsync(_runId, "fetch_orders")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('fetch_orders').output.nonexistentField");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNullForStepThatExistsButHasNotCompletedYet()
    {
        // Arrange
        _outputs.GetStepOutputAsync(_runId, "fetch_orders")
            .Returns(new ValueTask<object?>(default(object?)));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('fetch_orders').output.orderId");

        // Assert
        Assert.Null(result);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThrowsFlowExpressionExceptionForUndeclaredStepKey()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<FlowExpressionException>(
            async () => await resolver.ResolveAsync("@steps('ghost_step').output.orderId"));
        Assert.Equal("ghost_step", ex.StepKey);
        Assert.Contains("ghost_step", ex.Message);
        Assert.Contains("ghost_step", ex.Expression);
    }

    // ── Scope-relative sibling resolution inside a ForEach (issue #166) ─────────

    [Fact]
    public async Task ResolvesSiblingOutputByBareKeyInsideLoopScope()
    {
        // Arrange — open_camera (running as scan.0.open_camera) references its sibling
        // wait_robot_goto by bare key; the output is persisted under the runtime key.
        var output = Json("{\"Location\":\"BAY-7\"}");
        _outputs.GetStepOutputAsync(_runId, "scan.0.wait_robot_goto")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver(_loopSteps, "scan.0.open_camera");

        // Act
        var result = await resolver.ResolveAsync("@steps('wait_robot_goto').output.Location");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("BAY-7", element.GetString());
    }

    [Fact]
    public async Task ResolvesSiblingByBareKeyUsingIterationOfCurrentStep()
    {
        // Arrange — the current step is in iteration 3, so the sibling must resolve to
        // scan.3.wait_robot_goto (not scan.0.*), proving the loop index is honored.
        var output = Json("{\"Location\":\"BAY-3\"}");
        _outputs.GetStepOutputAsync(_runId, "scan.3.wait_robot_goto")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver(_loopSteps, "scan.3.open_camera");

        // Act
        var result = await resolver.ResolveAsync("@steps('wait_robot_goto').output.Location");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("BAY-3", element.GetString());
    }

    [Fact]
    public async Task TopLevelReferenceFromInsideLoopStillResolvesByBareKey()
    {
        // Arrange — a bare key that IS a top-level manifest step must not be rewritten
        // into the loop scope; it resolves against the top-level runtime key.
        var output = Json("{\"orderId\":\"ORD-9\"}");
        _outputs.GetStepOutputAsync(_runId, "fetch_orders")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver(_loopSteps, "scan.0.open_camera");

        // Act
        var result = await resolver.ResolveAsync("@steps('fetch_orders').output.orderId");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("ORD-9", element.GetString());
    }

    [Fact]
    public async Task ExplicitlyQualifiedRuntimeKeyResolvesDirectly()
    {
        // Arrange
        var output = Json("{\"Location\":\"BAY-1\"}");
        _outputs.GetStepOutputAsync(_runId, "scan.1.wait_robot_goto")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver(_loopSteps, "scan.1.open_camera");

        // Act
        var result = await resolver.ResolveAsync("@steps('scan.1.wait_robot_goto').output.Location");

        // Assert
        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal("BAY-1", element.GetString());
    }

    [Fact]
    public async Task SiblingStatusResolvesByBareKeyInsideLoopScope()
    {
        // Arrange
        var detail = new FlowRunRecord
        {
            Id = _runId,
            Status = "Running",
            Steps =
            [
                new FlowStepRecord { StepKey = "scan.2.wait_robot_goto", Status = "Succeeded" }
            ]
        };
        _runStore.GetRunDetailAsync(_runId).Returns(Task.FromResult<FlowRunRecord?>(detail));
        var resolver = CreateResolver(_loopSteps, "scan.2.open_camera");

        // Act
        var result = await resolver.ResolveAsync("@steps('wait_robot_goto').status");

        // Assert
        Assert.Equal("Succeeded", result);
    }

    [Fact]
    public async Task BareSiblingKeyStillThrowsWhenNoCurrentStepScopeIsSupplied()
    {
        // Arrange — without the current step key (the legacy 4-arg constructor), a bare
        // loop-child key cannot be scope-resolved and must still throw, unchanged.
        var resolver = CreateResolver(_loopSteps);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<FlowExpressionException>(
            async () => await resolver.ResolveAsync("@steps('wait_robot_goto').output.Location"));
        Assert.Equal("wait_robot_goto", ex.StepKey);
    }

    // ── Status and error ──────────────────────────────────────────────────────

    [Fact]
    public async Task ResolvesStatusToStringRepresentation()
    {
        // Arrange
        var detail = new FlowRunRecord
        {
            Id = _runId,
            Status = "Running",
            Steps =
            [
                new FlowStepRecord { StepKey = "fetch_orders", Status = "Succeeded" }
            ]
        };
        _runStore.GetRunDetailAsync(_runId).Returns(Task.FromResult<FlowRunRecord?>(detail));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('fetch_orders').status");

        // Assert
        Assert.Equal("Succeeded", result);
    }

    [Fact]
    public async Task ResolvesErrorToNullForSucceededStep()
    {
        // Arrange
        var detail = new FlowRunRecord
        {
            Id = _runId,
            Status = "Running",
            Steps =
            [
                new FlowStepRecord { StepKey = "fetch_orders", Status = "Succeeded", ErrorMessage = null }
            ]
        };
        _runStore.GetRunDetailAsync(_runId).Returns(Task.FromResult<FlowRunRecord?>(detail));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('fetch_orders').error");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolvesErrorToMessageForFailedStep()
    {
        // Arrange
        var detail = new FlowRunRecord
        {
            Id = _runId,
            Status = "Running",
            Steps =
            [
                new FlowStepRecord { StepKey = "submit", Status = "Failed", ErrorMessage = "Connection refused" }
            ]
        };
        _runStore.GetRunDetailAsync(_runId).Returns(Task.FromResult<FlowRunRecord?>(detail));
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@steps('submit').error");

        // Assert
        Assert.Equal("Connection refused", result);
    }

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoExpressionsReferencingSameStepTriggerOnlyOneRepositoryCall()
    {
        // Arrange
        var output = Json("{\"orderId\":\"ORD-1\",\"total\":99}");
        _outputs.GetStepOutputAsync(_runId, "fetch_orders")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver();

        // Act
        _ = await resolver.ResolveAsync("@steps('fetch_orders').output.orderId");
        _ = await resolver.ResolveAsync("@steps('fetch_orders').output.total");

        // Assert
        await _outputs.Received(1).GetStepOutputAsync(_runId, "fetch_orders");
    }

    // ── Quote style ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SingleQuoteAndDoubleQuoteBothWorkInStepName()
    {
        // Arrange
        var output = Json("{\"value\":\"ok\"}");
        _outputs.GetStepOutputAsync(_runId, "fetch_orders")
            .Returns(new ValueTask<object?>(output));
        var resolver = CreateResolver();

        // Act
        var resultSingle = await resolver.ResolveAsync("@steps('fetch_orders').output.value");
        var resultDouble = await resolver.ResolveAsync("@steps(\"fetch_orders\").output.value");

        // Assert
        var single = Assert.IsType<JsonElement>(resultSingle);
        var dbl = Assert.IsType<JsonElement>(resultDouble);
        Assert.Equal("ok", single.GetString());
        Assert.Equal("ok", dbl.GetString());
    }

    // ── Passthrough guard ─────────────────────────────────────────────────────

    [Fact]
    public async Task NonStepExpressionIsPassedThroughUnchanged()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var result = await resolver.ResolveAsync("@triggerBody().orderId");

        // Assert
        Assert.Equal("@triggerBody().orderId", result);
    }
}
