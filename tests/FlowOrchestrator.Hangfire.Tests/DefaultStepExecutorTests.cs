using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FlowOrchestrator.Hangfire.Tests;

public class DefaultStepExecutorTests
{
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();

    private static IFlowDefinition CreateFlow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private DefaultStepExecutor CreateExecutor(params IStepHandlerMetadata[] handlers)
    {
        return new DefaultStepExecutor(handlers, _serviceProvider, _outputsRepo, _runStore);
    }

    [Fact]
    public async Task ExecuteAsync_StepMetadataNotFound_ReturnsSkipped()
    {
        // Arrange
        var flow = CreateFlow(new StepCollection());
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("missing_step", "SomeType") { RunId = ctx.RunId };
        var executor = CreateExecutor();

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Contains("not found", result.FailedReason ?? string.Empty);
    }

    [Fact]
    public async Task ExecuteAsync_NoHandlerRegistered_ReturnsSkipped()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "UnknownType" }
        };
        var flow = CreateFlow(steps);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "UnknownType") { RunId = ctx.RunId };
        var executor = CreateExecutor();

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Contains("No handler", result.FailedReason ?? string.Empty);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerFound_DelegatesToHandler()
    {
        // Arrange
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

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal("step1", result.Key);
        await handlerMeta.Received(1).ExecuteAsync(_serviceProvider, ctx, flow, step);
    }

    [Fact]
    public async Task ExecuteAsync_SavesStepInput()
    {
        // Arrange
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

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        await _outputsRepo.Received(1).SaveStepInputAsync(ctx, flow, step);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerTypeMatch_IsCaseInsensitive()
    {
        // Arrange
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

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesStandaloneTriggerBodyExpression()
    {
        // Arrange
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

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.True(step.Inputs.ContainsKey("payload"));
        var payload = Assert.IsType<JsonElement>(step.Inputs["payload"]);
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal("ORD-1", payload.GetProperty("orderId").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesStandaloneTriggerBodyExpression_FromJsonElementString()
    {
        // Arrange
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

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        var payload = Assert.IsType<JsonElement>(step.Inputs["payload"]);
        Assert.Equal("ORD-2", payload.GetProperty("orderId").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesTriggerHeadersExpression_ForSpecificHeader()
    {
        // Arrange
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

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal("req-42", step.Inputs["requestId"]);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesTriggerHeadersExpression_ForAllHeaders()
    {
        // Arrange
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

        // Act
        await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        var headersElement = Assert.IsType<JsonElement>(step.Inputs["headers"]);
        var headerMap = headersElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("corr-1", headerMap["X-Correlation-Id"]);
        Assert.Equal("application/json", headerMap["Content-Type"]);
    }

    [Fact]
    public async Task ExecuteAsync_InputContainsUndefinedJsonElement_ResolvesToNull()
    {
        // Arrange
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

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Null(step.Inputs["payload"]);
    }

    [Fact]
    public async Task ExecuteAsync_GenericHandler_CoercesStringValuesToTypedProperties()
    {
        // Arrange
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
        var executor = new DefaultStepExecutor(metadata, serviceProvider, _outputsRepo, _runStore);

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.NotNull(CoercionTypedHandler.LastInput);
        Assert.True(CoercionTypedHandler.LastInput!.PollEnabled);
        Assert.Equal(5, CoercionTypedHandler.LastInput.PollIntervalSeconds);
        Assert.Equal(2, CoercionTypedHandler.LastInput.PollAttempt);
    }

    [Fact]
    public async Task ExecuteAsync_GenericHandler_SyncsMutatedInputsBackToStep()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddStepHandler<PollStateMutatingHandler>("PollStateMutatingHandler");
        var serviceProvider = services.BuildServiceProvider();

        var flow = CreateFlow(new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "PollStateMutatingHandler" }
        });

        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "PollStateMutatingHandler")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["pollEnabled"] = true,
                ["pollIntervalSeconds"] = 5,
                ["pollTimeoutSeconds"] = 30,
                ["pollMinAttempts"] = 1
            }
        };

        var metadata = serviceProvider.GetServices<IStepHandlerMetadata>();
        var executor = new DefaultStepExecutor(metadata, serviceProvider, _outputsRepo, _runStore);

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Pending, result.Status);
        Assert.True(step.Inputs.ContainsKey("__pollAttempt"));
        Assert.True(step.Inputs.ContainsKey("__pollStartedAtUtc"));
        Assert.Equal(5, ReadInt(step.Inputs["__pollAttempt"]));
        Assert.Equal(PollStateMutatingHandler.NextStartedAtUtc, ReadString(step.Inputs["__pollStartedAtUtc"]));
    }

    [Fact]
    public async Task ExecuteAsync_GenericHandler_BindsStrongTypedInput()
    {
        // Arrange
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
        var executor = new DefaultStepExecutor(metadata, serviceProvider, _outputsRepo, _runStore);

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal("JOB-123:4", result.Result);
        Assert.Equal(1, GenericTypedHandler.ExecutionCount);
        Assert.NotNull(GenericTypedHandler.LastInput);
        Assert.Equal("JOB-123", GenericTypedHandler.LastInput!.JobId);
        Assert.Equal(4, GenericTypedHandler.LastInput.Attempt);
    }

    [Fact]
    public async Task ExecuteAsync_GenericHandler_InputDeserializeFailure_ReturnsFailedResult()
    {
        // Arrange
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
        var executor = new DefaultStepExecutor(metadata, serviceProvider, _outputsRepo, _runStore);

        // Act
        var result = await executor.ExecuteAsync(ctx, flow, step);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("Failed to deserialize inputs", result.FailedReason ?? string.Empty);
        Assert.Contains("step1", result.FailedReason ?? string.Empty);
        Assert.Equal(0, GenericTypedHandler.ExecutionCount);
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

    private sealed class PollStateMutatingHandler : IStepHandler<PollStateMutatingInput>
    {
        internal const string NextStartedAtUtc = "2026-01-02T03:04:05.0000000+00:00";

        public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance<PollStateMutatingInput> step)
        {
            step.Inputs.PollAttempt = 5;
            step.Inputs.PollStartedAtUtc = NextStartedAtUtc;
            return ValueTask.FromResult<object?>(new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Pending,
                DelayNextStep = TimeSpan.FromSeconds(5)
            });
        }
    }

    private sealed class PollStateMutatingInput
    {
        public bool PollEnabled { get; set; }
        public int PollIntervalSeconds { get; set; }
        public int PollTimeoutSeconds { get; set; }
        public int PollMinAttempts { get; set; }

        [JsonPropertyName("__pollStartedAtUtc")]
        public string? PollStartedAtUtc { get; set; }

        [JsonPropertyName("__pollAttempt")]
        public int? PollAttempt { get; set; }
    }

    private static int ReadInt(object? value)
    {
        return value switch
        {
            int i => i,
            JsonElement { ValueKind: JsonValueKind.Number } json => json.GetInt32(),
            JsonElement { ValueKind: JsonValueKind.String } json => int.Parse(json.GetString()!, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Cannot convert value '{value}' to int.")
        };
    }

    private static string? ReadString(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => throw new InvalidOperationException($"Cannot convert value '{value}' to string.")
        };
    }
}
