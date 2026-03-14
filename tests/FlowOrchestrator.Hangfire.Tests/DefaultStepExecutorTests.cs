using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FluentAssertions;
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

        result.Status.Should().Be("Skipped");
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

        result.Status.Should().Be("Skipped");
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
            .Returns(new StepResult { Key = "step1", Status = "Succeeded", Result = "done" });

        var executor = CreateExecutor(handlerMeta);

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be("Succeeded");
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
            .ReturnsForAnyArgs(new StepResult { Key = "step1", Status = "Succeeded" });

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
            .ReturnsForAnyArgs(new StepResult { Key = "step1", Status = "Succeeded" });

        var executor = CreateExecutor(handlerMeta);

        var result = await executor.ExecuteAsync(ctx, flow, step);

        result.Status.Should().Be("Succeeded");
    }
}
