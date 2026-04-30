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

    private StepOutputResolver CreateResolver(StepCollection? steps = null) =>
        new(_outputs, _runStore, _runId, steps ?? _defaultSteps);

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
