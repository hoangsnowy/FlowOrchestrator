using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FlowOrchestrator.Hangfire.Tests;

public class DefaultStepExecutorTests
{
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();

    private static IFlowDefinition CreateFlow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private DefaultStepExecutor CreateExecutor(params IStepHandlerMetadata[] handlers)
    {
        return new DefaultStepExecutor(handlers, _serviceProvider, _outputsRepo);
    }

    [Fact]
    public async Task ExecuteAsync_StepMetadataNotFound_ReturnsSkipped()
    {
        var flow = CreateFlow(new StepCollection());
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("missing_step", "SomeType") { RunId = ctx.RunId };
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be(StepStatus.Skipped);
        result.FailedReason.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_NoHandlerRegistered_ReturnsSkipped()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "UnknownType" }
        };
        var flow = CreateFlow(steps);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "UnknownType") { RunId = ctx.RunId };
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be(StepStatus.Skipped);
        result.FailedReason.Should().Contain("No handler");
    }

    [Fact]
    public async Task ExecuteAsync_HandlerFound_DelegatesToHandler()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "LogMessage" }
        };
        var flow = CreateFlow(steps);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        var handlerMeta = Substitute.For<IStepHandlerMetadata>();
        handlerMeta.Type.Returns("LogMessage");
        handlerMeta.ExecuteAsync(_serviceProvider, ctx, flow, step)
            .Returns(new StepResult { Key = "step1", Status = StepStatus.Succeeded, Result = "done" });

        var executor = CreateExecutor(handlerMeta);

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be(StepStatus.Succeeded);
        result.Key.Should().Be("step1");
        await handlerMeta.Received(1).ExecuteAsync(_serviceProvider, ctx, flow, step);
    }

    [Fact]
    public async Task ExecuteAsync_SavesStepInput()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "LogMessage" }
        };
        var flow = CreateFlow(steps);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        var handlerMeta = Substitute.For<IStepHandlerMetadata>();
        handlerMeta.Type.Returns("LogMessage");
        handlerMeta.ExecuteAsync(default!, default!, default!, default!)
            .ReturnsForAnyArgs(new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        var executor = CreateExecutor(handlerMeta);

        await executor.ExecuteAsync(ctx, flow, step);

        await _outputsRepo.Received(1).SaveStepInputAsync(ctx, flow, step);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerTypeMatch_IsCaseInsensitive()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "logmessage" }
        };
        var flow = CreateFlow(steps);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "logmessage") { RunId = ctx.RunId };

        var handlerMeta = Substitute.For<IStepHandlerMetadata>();
        handlerMeta.Type.Returns("LogMessage");
        handlerMeta.ExecuteAsync(default!, default!, default!, default!)
            .ReturnsForAnyArgs(new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        var executor = CreateExecutor(handlerMeta);

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be(StepStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesStandaloneTriggerBodyExpression()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "LogMessage" }
        };
        var flow = CreateFlow(steps);
        var triggerData = new { orderId = "ORD-1", total = 100 };
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = triggerData };
        var step = new StepInstance("step1", "LogMessage")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?> { ["payload"] = "@triggerBody()" }
        };

        var handlerMeta = Substitute.For<IStepHandlerMetadata>();
        handlerMeta.Type.Returns("LogMessage");
        handlerMeta.ExecuteAsync(default!, default!, default!, default!)
            .ReturnsForAnyArgs(new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        var executor = CreateExecutor(handlerMeta);

        await executor.ExecuteAsync(ctx, flow, step);

        step.Inputs.Should().ContainKey("payload");
        step.Inputs["payload"].Should().BeOfType<JsonElement>();
        var payload = (JsonElement)step.Inputs["payload"]!;
        payload.ValueKind.Should().Be(JsonValueKind.Object);
        payload.GetProperty("orderId").GetString().Should().Be("ORD-1");
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesStandaloneTriggerBodyExpression_FromJsonElementString()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "LogMessage" }
        };
        var flow = CreateFlow(steps);
        var triggerData = JsonSerializer.Deserialize<JsonElement>("{\"orderId\":\"ORD-2\",\"items\":[1,2]}");
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = triggerData };
        var step = new StepInstance("step1", "LogMessage")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["payload"] = JsonSerializer.Deserialize<JsonElement>("\"@triggerBody()\"")
            }
        };

        var handlerMeta = Substitute.For<IStepHandlerMetadata>();
        handlerMeta.Type.Returns("LogMessage");
        handlerMeta.ExecuteAsync(default!, default!, default!, default!)
            .ReturnsForAnyArgs(new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        var executor = CreateExecutor(handlerMeta);

        await executor.ExecuteAsync(ctx, flow, step);

        step.Inputs["payload"].Should().BeOfType<JsonElement>();
        var payload = (JsonElement)step.Inputs["payload"]!;
        payload.GetProperty("orderId").GetString().Should().Be("ORD-2");
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesTriggerHeadersExpression_ForSpecificHeader()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "LogMessage" }
        };
        var flow = CreateFlow(steps);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Request-Id"] = "req-42"
        };
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerHeaders = headers };
        var step = new StepInstance("step1", "LogMessage")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["requestId"] = "@triggerHeaders()['X-Request-Id']"
            }
        };

        var handlerMeta = Substitute.For<IStepHandlerMetadata>();
        handlerMeta.Type.Returns("LogMessage");
        handlerMeta.ExecuteAsync(default!, default!, default!, default!)
            .ReturnsForAnyArgs(new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        var executor = CreateExecutor(handlerMeta);

        await executor.ExecuteAsync(ctx, flow, step);

        step.Inputs["requestId"].Should().Be("req-42");
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesTriggerHeadersExpression_ForAllHeaders()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "LogMessage" }
        };
        var flow = CreateFlow(steps);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Correlation-Id"] = "corr-1",
            ["Content-Type"] = "application/json"
        };
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerHeaders = headers };
        var step = new StepInstance("step1", "LogMessage")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["headers"] = JsonSerializer.Deserialize<JsonElement>("\"@triggerHeaders()\"")
            }
        };

        var handlerMeta = Substitute.For<IStepHandlerMetadata>();
        handlerMeta.Type.Returns("LogMessage");
        handlerMeta.ExecuteAsync(default!, default!, default!, default!)
            .ReturnsForAnyArgs(new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        var executor = CreateExecutor(handlerMeta);

        await executor.ExecuteAsync(ctx, flow, step);

        step.Inputs["headers"].Should().BeOfType<JsonElement>();
        var headersElement = (JsonElement)step.Inputs["headers"]!;
        var headerMap = headersElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        headerMap["X-Correlation-Id"].Should().Be("corr-1");
        headerMap["Content-Type"].Should().Be("application/json");
    }

    [Fact]
    public async Task ExecuteAsync_InputContainsUndefinedJsonElement_ResolvesToNull()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "LogMessage" }
        };
        var flow = CreateFlow(steps);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["payload"] = default(JsonElement)
            }
        };

        var handlerMeta = Substitute.For<IStepHandlerMetadata>();
        handlerMeta.Type.Returns("LogMessage");
        handlerMeta.ExecuteAsync(default!, default!, default!, default!)
            .ReturnsForAnyArgs(new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        var executor = CreateExecutor(handlerMeta);

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be(StepStatus.Succeeded);
        step.Inputs["payload"].Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_GenericHandler_CoercesStringValuesToTypedProperties()
    {
        CoercionTypedHandler.Reset();

        var services = new ServiceCollection();
        services.AddStepHandler<CoercionTypedHandler>("CoercionTypedHandler");
        var serviceProvider = services.BuildServiceProvider();

        var flow = CreateFlow(new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "CoercionTypedHandler" }
        });

        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "CoercionTypedHandler")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["pollEnabled"] = JsonSerializer.Deserialize<JsonElement>("\"true\""),
                ["pollIntervalSeconds"] = JsonSerializer.Deserialize<JsonElement>("\"5\""),
                ["__pollAttempt"] = JsonSerializer.Deserialize<JsonElement>("\"2\"")
            }
        };

        var metadata = serviceProvider.GetServices<IStepHandlerMetadata>();
        var executor = new DefaultStepExecutor(metadata, serviceProvider, _outputsRepo);

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be(StepStatus.Succeeded);
        CoercionTypedHandler.LastInput.Should().NotBeNull();
        CoercionTypedHandler.LastInput!.PollEnabled.Should().BeTrue();
        CoercionTypedHandler.LastInput.PollIntervalSeconds.Should().Be(5);
        CoercionTypedHandler.LastInput.PollAttempt.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_GenericHandler_BindsStrongTypedInput()
    {
        GenericTypedHandler.Reset();

        var services = new ServiceCollection();
        services.AddStepHandler<GenericTypedHandler>("TypedHandler");
        var serviceProvider = services.BuildServiceProvider();

        var flow = CreateFlow(new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "TypedHandler" }
        });

        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "TypedHandler")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["jobId"] = "JOB-123",
                ["attempt"] = 4
            }
        };

        var metadata = serviceProvider.GetServices<IStepHandlerMetadata>();
        var executor = new DefaultStepExecutor(metadata, serviceProvider, _outputsRepo);

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be(StepStatus.Succeeded);
        result.Result.Should().Be("JOB-123:4");
        GenericTypedHandler.ExecutionCount.Should().Be(1);
        GenericTypedHandler.LastInput.Should().NotBeNull();
        GenericTypedHandler.LastInput!.JobId.Should().Be("JOB-123");
        GenericTypedHandler.LastInput.Attempt.Should().Be(4);
    }

    [Fact]
    public async Task ExecuteAsync_GenericHandler_InputDeserializeFailure_ReturnsFailedResult()
    {
        GenericTypedHandler.Reset();

        var services = new ServiceCollection();
        services.AddStepHandler<GenericTypedHandler>("TypedHandler");
        var serviceProvider = services.BuildServiceProvider();

        var flow = CreateFlow(new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "TypedHandler" }
        });

        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "TypedHandler")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["jobId"] = "JOB-123",
                ["attempt"] = "not-a-number"
            }
        };

        var metadata = serviceProvider.GetServices<IStepHandlerMetadata>();
        var executor = new DefaultStepExecutor(metadata, serviceProvider, _outputsRepo);

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailedReason.Should().Contain("Failed to deserialize inputs");
        result.FailedReason.Should().Contain("step1");
        GenericTypedHandler.ExecutionCount.Should().Be(0);
    }

    private sealed class GenericTypedHandler : IStepHandler<GenericTypedInput>
    {
        public static int ExecutionCount { get; private set; }
        public static GenericTypedInput? LastInput { get; private set; }

        public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance<GenericTypedInput> step)
        {
            ExecutionCount++;
            LastInput = step.Inputs;
            return ValueTask.FromResult<object?>($"{step.Inputs.JobId}:{step.Inputs.Attempt}");
        }

        public static void Reset()
        {
            ExecutionCount = 0;
            LastInput = null;
        }
    }

    private sealed class GenericTypedInput
    {
        public string JobId { get; set; } = string.Empty;
        public int Attempt { get; set; }
    }

    private sealed class CoercionTypedHandler : IStepHandler<CoercionTypedInput>
    {
        public static CoercionTypedInput? LastInput { get; private set; }

        public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance<CoercionTypedInput> step)
        {
            LastInput = step.Inputs;
            return ValueTask.FromResult<object?>(new StepResult { Key = step.Key, Status = StepStatus.Succeeded });
        }

        public static void Reset()
        {
            LastInput = null;
        }
    }

    private sealed class CoercionTypedInput
    {
        public bool PollEnabled { get; set; }
        public int PollIntervalSeconds { get; set; }

        [JsonPropertyName("__pollAttempt")]
        public int? PollAttempt { get; set; }
    }
}
