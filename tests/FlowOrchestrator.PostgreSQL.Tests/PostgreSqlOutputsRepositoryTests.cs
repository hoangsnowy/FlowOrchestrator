using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FluentAssertions;
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
        var runId = Guid.NewGuid();
        var ctx = MakeTriggerContext(runId);
        var flow = Substitute.For<IFlowDefinition>();
        var trigger = Substitute.For<ITrigger>();
        trigger.Data.Returns(new { OrderId = 42, Customer = "Test" });
        trigger.Headers.Returns((IReadOnlyDictionary<string, string>?)null);

        await _repo.SaveTriggerDataAsync(ctx, flow, trigger);
        var result = await _repo.GetTriggerDataAsync(runId);

        result.Should().NotBeNull();
        result.Should().BeOfType<JsonElement>();
        var je = (JsonElement)result!;
        je.GetProperty("orderId").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task GetTriggerDataAsync_returns_null_for_unknown_run()
    {
        var result = await _repo.GetTriggerDataAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveTriggerHeadersAsync_then_GetTriggerHeadersAsync_round_trip()
    {
        var runId = Guid.NewGuid();
        var ctx = MakeTriggerContext(runId);
        var flow = Substitute.For<IFlowDefinition>();
        var trigger = Substitute.For<ITrigger>();
        trigger.Data.Returns((object?)null);
        var headers = new Dictionary<string, string> { ["X-Request-Id"] = "abc123" };
        trigger.Headers.Returns((IReadOnlyDictionary<string, string>)headers);

        await _repo.SaveTriggerHeadersAsync(ctx, flow, trigger);
        var result = await _repo.GetTriggerHeadersAsync(runId);

        result.Should().NotBeNull();
        result!["X-Request-Id"].Should().Be("abc123");
    }

    [Fact]
    public async Task GetTriggerHeadersAsync_returns_null_when_no_headers_saved()
    {
        var result = await _repo.GetTriggerHeadersAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveStepOutputAsync_then_GetStepOutputAsync_round_trip()
    {
        var runId = Guid.NewGuid();
        var ctx = MakeExecutionContext(runId);
        var flow = Substitute.For<IFlowDefinition>();
        var step = Substitute.For<IStepInstance>();
        step.Key.Returns("step1");
        var result = Substitute.For<IStepResult>();
        result.Result.Returns((object?)new { Value = 99 });

        await _repo.SaveStepOutputAsync(ctx, flow, step, result);
        var output = await _repo.GetStepOutputAsync(runId, "step1");

        output.Should().NotBeNull();
        var je = (JsonElement)output!;
        je.GetProperty("value").GetInt32().Should().Be(99);
    }

    [Fact]
    public async Task GetStepOutputAsync_returns_null_for_unknown_key()
    {
        var result = await _repo.GetStepOutputAsync(Guid.NewGuid(), "nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveStepOutputAsync_overwrites_on_second_save()
    {
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
        await _repo.SaveStepOutputAsync(ctx, flow, step, result2);

        var output = await _repo.GetStepOutputAsync(runId, "overwrite-step");
        var je = (JsonElement)output!;
        je.GetProperty("value").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task EndScopeAsync_completes_without_error()
    {
        var ctx = MakeExecutionContext(Guid.NewGuid());
        var flow = Substitute.For<IFlowDefinition>();
        var step = Substitute.For<IStepInstance>();
        await _repo.EndScopeAsync(ctx, flow, step);
    }

    [Fact]
    public async Task RecordEventAsync_completes_without_error()
    {
        var ctx = MakeExecutionContext(Guid.NewGuid());
        var flow = Substitute.For<IFlowDefinition>();
        var step = Substitute.For<IStepInstance>();
        await _repo.RecordEventAsync(ctx, flow, step, new FlowEvent { Type = "test" });
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
