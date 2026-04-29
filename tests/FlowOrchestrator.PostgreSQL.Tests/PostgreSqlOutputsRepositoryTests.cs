using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.PostgreSQL.Tests;

public sealed class PostgreSqlOutputsRepositoryTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlOutputsRepository _repo;

    public PostgreSqlOutputsRepositoryTests(PostgreSqlFixture fixture)
    {
        _repo = new PostgreSqlOutputsRepository(fixture.ConnectionString);
    }

    [Fact]
    public async Task SaveTriggerDataAsync_then_GetTriggerDataAsync_round_trip()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var ctx = MakeTriggerContext(runId);
        var flow = Substitute.For<IFlowDefinition>();
        var trigger = Substitute.For<ITrigger>();
        trigger.Data.Returns(new { OrderId = 42, Customer = "Test" });
        trigger.Headers.Returns((IReadOnlyDictionary<string, string>?)null);

        // Act
        await _repo.SaveTriggerDataAsync(ctx, flow, trigger);
        var result = await _repo.GetTriggerDataAsync(runId);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<JsonElement>(result);
        var je = (JsonElement)result!;
        Assert.Equal(42, je.GetProperty("orderId").GetInt32());
    }

    [Fact]
    public async Task GetTriggerDataAsync_returns_null_for_unknown_run()
    {
        // Arrange

        // Act
        var result = await _repo.GetTriggerDataAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveTriggerHeadersAsync_then_GetTriggerHeadersAsync_round_trip()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var ctx = MakeTriggerContext(runId);
        var flow = Substitute.For<IFlowDefinition>();
        var trigger = Substitute.For<ITrigger>();
        trigger.Data.Returns((object?)null);
        var headers = new Dictionary<string, string> { ["X-Request-Id"] = "abc123" };
        trigger.Headers.Returns((IReadOnlyDictionary<string, string>)headers);

        // Act
        await _repo.SaveTriggerHeadersAsync(ctx, flow, trigger);
        var result = await _repo.GetTriggerHeadersAsync(runId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("abc123", result!["X-Request-Id"]);
    }

    [Fact]
    public async Task GetTriggerHeadersAsync_returns_null_when_no_headers_saved()
    {
        // Arrange

        // Act
        var result = await _repo.GetTriggerHeadersAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveStepOutputAsync_then_GetStepOutputAsync_round_trip()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var ctx = MakeExecutionContext(runId);
        var flow = Substitute.For<IFlowDefinition>();
        var step = Substitute.For<IStepInstance>();
        step.Key.Returns("step1");
        var result = Substitute.For<IStepResult>();
        result.Result.Returns((object?)new { Value = 99 });

        // Act
        await _repo.SaveStepOutputAsync(ctx, flow, step, result);
        var output = await _repo.GetStepOutputAsync(runId, "step1");

        // Assert
        Assert.NotNull(output);
        var je = (JsonElement)output!;
        Assert.Equal(99, je.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task GetStepOutputAsync_returns_null_for_unknown_key()
    {
        // Arrange

        // Act
        var result = await _repo.GetStepOutputAsync(Guid.NewGuid(), "nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveStepOutputAsync_overwrites_on_second_save()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var ctx = MakeExecutionContext(runId);
        var flow = Substitute.For<IFlowDefinition>();
        var step = Substitute.For<IStepInstance>();
        step.Key.Returns("overwrite-step");

        var result1 = Substitute.For<IStepResult>();
        result1.Result.Returns((object?)new { Value = 1 });
        await _repo.SaveStepOutputAsync(ctx, flow, step, result1);

        var result2 = Substitute.For<IStepResult>();
        result2.Result.Returns((object?)new { Value = 2 });

        // Act
        await _repo.SaveStepOutputAsync(ctx, flow, step, result2);
        var output = await _repo.GetStepOutputAsync(runId, "overwrite-step");

        // Assert
        var je = (JsonElement)output!;
        Assert.Equal(2, je.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task EndScopeAsync_completes_without_error()
    {
        // Arrange
        var ctx = MakeExecutionContext(Guid.NewGuid());
        var flow = Substitute.For<IFlowDefinition>();
        var step = Substitute.For<IStepInstance>();

        // Act
        var ex = await Record.ExceptionAsync(() => _repo.EndScopeAsync(ctx, flow, step).AsTask());

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task RecordEventAsync_completes_without_error()
    {
        // Arrange
        var ctx = MakeExecutionContext(Guid.NewGuid());
        var flow = Substitute.For<IFlowDefinition>();
        var step = Substitute.For<IStepInstance>();

        // Act
        var ex = await Record.ExceptionAsync(() => _repo.RecordEventAsync(ctx, flow, step, new FlowEvent { Type = "test" }).AsTask());

        // Assert
        Assert.Null(ex);
    }

    private static ITriggerContext MakeTriggerContext(Guid runId)
    {
        var ctx = Substitute.For<ITriggerContext>();
        ctx.RunId.Returns(runId);
        return ctx;
    }

    private static IExecutionContext MakeExecutionContext(Guid runId)
    {
        var ctx = Substitute.For<IExecutionContext>();
        ctx.RunId.Returns(runId);
        return ctx;
    }
}
