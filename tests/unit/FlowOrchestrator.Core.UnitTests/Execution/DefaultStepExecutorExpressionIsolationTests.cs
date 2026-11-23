using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Locks the contract that only text authored in the flow manifest is evaluated as an expression.
/// A value substituted in from the trigger payload, the trigger headers, or a prior step's output
/// is data, and must reach the handler verbatim even when it reads like an expression — on the
/// first attempt and on any later attempt that re-runs the same, already-resolved instance.
/// </summary>
public class DefaultStepExecutorExpressionIsolationTests
{
    private const string StepsExpression = "@steps('source').output.secret";
    private const string AuthorizationExpression = "@triggerHeaders()['Authorization']";

    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IOutputsRepository _outputs = Substitute.For<IOutputsRepository>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly Guid _runId = Guid.NewGuid();

    private static IFlowDefinition CreateFlow(IDictionary<string, object?> consumerInputs)
    {
        var steps = new StepCollection
        {
            ["source"] = new StepMetadata { Type = "Source" },
            ["consumer"] = new StepMetadata
            {
                Type = "Consumer",
                RunAfter = new RunAfterCollection { { "source", [StepStatus.Succeeded] } },
                Inputs = consumerInputs
            }
        };

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private DefaultStepExecutor CreateExecutor(Action<IDictionary<string, object?>> capture)
    {
        var handler = Substitute.For<IStepHandlerMetadata>();
        handler.Type.Returns("Consumer");
        handler
            .ExecuteAsync(
                Arg.Any<IServiceProvider>(),
                Arg.Any<IExecutionContext>(),
                Arg.Any<IFlowDefinition>(),
                Arg.Do<IStepInstance>(
                    instance => capture(new Dictionary<string, object?>(instance.Inputs, StringComparer.Ordinal))))
            .Returns(new ValueTask<IStepResult>(new StepResult { Key = "consumer", Status = StepStatus.Succeeded }));

        return new DefaultStepExecutor([handler], _serviceProvider, _outputs, _runStore);
    }

    private StepInstance CreateStep(IFlowDefinition flow) => new("consumer", "Consumer")
    {
        RunId = _runId,
        Inputs = new Dictionary<string, object?>(flow.Manifest.Steps.FindStep("consumer")!.Inputs)
    };

    private void SourceOutputReturns(string json)
        => _outputs.GetStepOutputAsync(_runId, "source")
            .Returns(new ValueTask<object?>(JsonSerializer.Deserialize<JsonElement>(json)));

    private static string? AsString(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null
    };

    [Fact]
    public async Task TriggerBodyValue_ThatReadsLikeStepsExpression_StaysLiteral()
    {
        // Arrange
        var flow = CreateFlow(new Dictionary<string, object?> { ["note"] = "@triggerBody()?.note" });
        var trigger = JsonSerializer.Deserialize<JsonElement>("{\"note\":\"@steps('source').output.secret\"}");
        var ctx = new Core.Execution.ExecutionContext { RunId = _runId, TriggerData = trigger };
        SourceOutputReturns("{\"secret\":\"s3cr3t\"}");

        IDictionary<string, object?>? observed = null;
        var executor = CreateExecutor(inputs => observed = inputs);

        // Act
        await executor.ExecuteAsync(ctx, flow, CreateStep(flow));

        // Assert
        Assert.NotNull(observed);
        Assert.Equal(StepsExpression, AsString(observed!["note"]));
        await _outputs.DidNotReceive().GetStepOutputAsync(Arg.Any<Guid>(), "source");
    }

    [Fact]
    public async Task TriggerHeaderValue_ThatReadsLikeStepsExpression_StaysLiteral()
    {
        // Arrange
        var flow = CreateFlow(new Dictionary<string, object?> { ["requestId"] = "@triggerHeaders()['X-Request-Id']" });
        var ctx = new Core.Execution.ExecutionContext
        {
            RunId = _runId,
            TriggerHeaders = new Dictionary<string, string> { ["X-Request-Id"] = StepsExpression }
        };
        SourceOutputReturns("{\"secret\":\"s3cr3t\"}");

        IDictionary<string, object?>? observed = null;
        var executor = CreateExecutor(inputs => observed = inputs);

        // Act
        await executor.ExecuteAsync(ctx, flow, CreateStep(flow));

        // Assert
        Assert.NotNull(observed);
        Assert.Equal(StepsExpression, AsString(observed!["requestId"]));
        await _outputs.DidNotReceive().GetStepOutputAsync(Arg.Any<Guid>(), "source");
    }

    [Fact]
    public async Task TriggerBodyObject_WithNestedStepsExpression_StaysLiteral()
    {
        // Arrange — the whole object is substituted; nothing inside it may be evaluated.
        var flow = CreateFlow(new Dictionary<string, object?> { ["customer"] = "@triggerBody()?.customer" });
        var trigger = JsonSerializer.Deserialize<JsonElement>(
            "{\"customer\":{\"name\":\"@steps('source').output.secret\",\"tags\":[\"@steps('source').status\"]}}");
        var ctx = new Core.Execution.ExecutionContext { RunId = _runId, TriggerData = trigger };

        IDictionary<string, object?>? observed = null;
        var executor = CreateExecutor(inputs => observed = inputs);

        // Act
        await executor.ExecuteAsync(ctx, flow, CreateStep(flow));

        // Assert
        Assert.NotNull(observed);
        var customer = Assert.IsType<JsonElement>(observed!["customer"]);
        Assert.Equal(StepsExpression, customer.GetProperty("name").GetString());
        Assert.Equal("@steps('source').status", customer.GetProperty("tags")[0].GetString());
        await _outputs.DidNotReceive().GetStepOutputAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _runStore.DidNotReceive().GetStepAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task StepOutput_ThatReadsLikeTriggerExpressions_StaysLiteral()
    {
        // Arrange
        var flow = CreateFlow(new Dictionary<string, object?>
        {
            ["body"] = "@steps('source').output.body",
            ["auth"] = "@steps('source').output.auth"
        });
        var trigger = JsonSerializer.Deserialize<JsonElement>("{\"x\":\"trigger-secret\"}");
        var ctx = new Core.Execution.ExecutionContext
        {
            RunId = _runId,
            TriggerData = trigger,
            TriggerHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer abc" }
        };
        SourceOutputReturns(
            "{\"body\":\"@triggerBody()?.x\",\"auth\":\"@triggerHeaders()['Authorization']\"}");

        IDictionary<string, object?>? observed = null;
        var executor = CreateExecutor(inputs => observed = inputs);

        // Act
        await executor.ExecuteAsync(ctx, flow, CreateStep(flow));

        // Assert
        Assert.NotNull(observed);
        Assert.Equal("@triggerBody()?.x", AsString(observed!["body"]));
        Assert.Equal(AuthorizationExpression, AsString(observed["auth"]));
    }

    [Fact]
    public async Task ReExecutingResolvedInstance_DoesNotEvaluateSubstitutedValues()
    {
        // Arrange — the Pending re-poll path re-dispatches the SAME instance with the inputs the
        // first attempt resolved; the second attempt must treat them as data too.
        var flow = CreateFlow(new Dictionary<string, object?>
        {
            ["note"] = "@triggerBody()?.note",
            ["auth"] = "@steps('source').output.auth"
        });
        var trigger = JsonSerializer.Deserialize<JsonElement>("{\"note\":\"@steps('source').output.secret\"}");
        var ctx = new Core.Execution.ExecutionContext
        {
            RunId = _runId,
            TriggerData = trigger,
            TriggerHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer abc" }
        };
        SourceOutputReturns("{\"secret\":\"s3cr3t\",\"auth\":\"@triggerHeaders()['Authorization']\"}");

        IDictionary<string, object?>? observed = null;
        var executor = CreateExecutor(inputs => observed = inputs);
        var step = CreateStep(flow);
        await executor.ExecuteAsync(ctx, flow, step);

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.NotNull(observed);
        Assert.Equal(StepsExpression, AsString(observed!["note"]));
        Assert.Equal(AuthorizationExpression, AsString(observed["auth"]));
    }

    [Fact]
    public async Task TypedWriteBackUnderDifferentCasing_IsStillTreatedAsData()
    {
        // Arrange — a typed handler writes its inputs back under camelCase names, so the key the
        // next attempt sees ("note") differs in case from the manifest key ("Note").
        var flow = CreateFlow(new Dictionary<string, object?> { ["Note"] = "@triggerBody()?.note" });
        var ctx = new Core.Execution.ExecutionContext { RunId = _runId };
        SourceOutputReturns("{\"secret\":\"s3cr3t\"}");

        IDictionary<string, object?>? observed = null;
        var executor = CreateExecutor(inputs => observed = inputs);
        var step = new StepInstance("consumer", "Consumer")
        {
            RunId = _runId,
            Inputs = new Dictionary<string, object?> { ["note"] = StepsExpression }
        };

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.NotNull(observed);
        Assert.Equal(StepsExpression, AsString(observed!["note"]));
        await _outputs.DidNotReceive().GetStepOutputAsync(Arg.Any<Guid>(), "source");
    }

    [Fact]
    public async Task NestedManifestDictionary_IsCopied_SoHandlersNeverShareTheFlowDefinition()
    {
        // Arrange — a nested dictionary of literals is still the manifest's own object.
        var nested = new Dictionary<string, object?> { ["region"] = "eu" };
        var flow = CreateFlow(new Dictionary<string, object?> { ["options"] = nested });
        var ctx = new Core.Execution.ExecutionContext { RunId = _runId };

        IDictionary<string, object?>? observed = null;
        var executor = CreateExecutor(inputs => observed = inputs);

        // Act
        await executor.ExecuteAsync(ctx, flow, CreateStep(flow));

        // Assert
        Assert.NotNull(observed);
        var options = Assert.IsAssignableFrom<IDictionary<string, object?>>(observed!["options"]);
        Assert.NotSame(nested, options);
        Assert.Equal("eu", options["region"]);
    }

    [Fact]
    public async Task ManifestExpressions_StillResolve_IncludingNestedAndSerializedInputs()
    {
        // Arrange — the manifest's own expressions, at the top level and nested, resolve as before,
        // including after the inputs went through a JSON round trip (Hangfire / Service Bus).
        var manifestInputs = new Dictionary<string, object?>
        {
            ["orderId"] = "@triggerBody()?.orderId",
            ["requestId"] = "@triggerHeaders()['X-Request-Id']",
            ["total"] = "@steps('source').output.total",
            ["nested"] = new Dictionary<string, object?>
            {
                ["status"] = "@steps('source').status",
                ["list"] = new object?[] { "@triggerBody()?.orderId", "literal" }
            }
        };
        var flow = CreateFlow(manifestInputs);
        var trigger = JsonSerializer.Deserialize<JsonElement>("{\"orderId\":\"ORD-7\"}");
        var ctx = new Core.Execution.ExecutionContext
        {
            RunId = _runId,
            TriggerData = trigger,
            TriggerHeaders = new Dictionary<string, string> { ["X-Request-Id"] = "req-1" }
        };
        SourceOutputReturns("{\"total\":42}");
        _runStore.GetStepAsync(_runId, "source")
            .Returns(Task.FromResult<FlowStepRecord?>(new FlowStepRecord { StepKey = "source", Status = "Succeeded" }));

        var observedRuns = new List<IDictionary<string, object?>>();
        var executor = CreateExecutor(observedRuns.Add);
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(manifestInputs))!;

        // Act
        await executor.ExecuteAsync(ctx, flow, CreateStep(flow));
        await executor.ExecuteAsync(ctx, flow, new StepInstance("consumer", "Consumer") { RunId = _runId, Inputs = roundTripped });

        // Assert
        Assert.Equal(2, observedRuns.Count);
        foreach (var observed in observedRuns)
        {
            Assert.Equal("ORD-7", AsString(observed["orderId"]));
            Assert.Equal("req-1", AsString(observed["requestId"]));
            Assert.Equal(42, Assert.IsType<JsonElement>(observed["total"]).GetInt32());
            var nested = Assert.IsType<Dictionary<string, object?>>(observed["nested"]);
            Assert.Equal("Succeeded", AsString(nested["status"]));
            var list = Assert.IsType<object?[]>(nested["list"]);
            Assert.Equal("ORD-7", AsString(list[0]));
            Assert.Equal("literal", AsString(list[1]));
        }
    }
}
