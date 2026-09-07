using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// Regression tests for known issue #3 — JSON property-naming divergence between
/// <see cref="InMemoryOutputsRepository"/> (which serialises step output via
/// <c>JsonSerializerDefaults.Web</c> → camelCase) and the engine's internal
/// <c>SafeSerialize</c> path (which uses default settings → PascalCase).
/// <para>
/// They lock in two facts:
/// <list type="number">
///   <item>The outputs repository still writes property names in camelCase regardless of POCO
///   casing — the storage format is unchanged.</item>
///   <item>The <see cref="StepOutputResolver"/> nevertheless resolves
///   <c>@steps('key').output.FieldName</c> against that camelCase record, because the path
///   walker falls back to a case-insensitive property match (issue #177). PascalCase access no
///   longer silently returns <see langword="null"/>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class JsonCasingDivergenceTests
{
    [Fact]
    public async Task SaveStepOutput_PocoWithPascalCaseProperties_PersistsAsCamelCase()
    {
        // Arrange — POCO with PascalCase property names.
        var repo = new InMemoryOutputsRepository();
        var runId = Guid.NewGuid();
        var ctx = new CoreExecutionContext { RunId = runId };
        var flow = MakeFlow();
        var step = new StepInstance("fetch", "Work") { RunId = runId };
        var result = new StepResult
        {
            Key = "fetch",
            Status = StepStatus.Succeeded,
            Result = new { OrderId = 42, CustomerName = "Alice" }
        };

        // Act
        await repo.SaveStepOutputAsync(ctx, flow, step, result);
        var raw = await repo.GetStepOutputAsync(runId, "fetch");

        // Assert — the persisted JsonElement uses camelCase property names.
        var element = Assert.IsType<JsonElement>(raw);
        Assert.True(element.TryGetProperty("orderId", out _),
            "Outputs repository should serialise PascalCase POCO properties as camelCase (web defaults).");
        Assert.False(element.TryGetProperty("OrderId", out _),
            "PascalCase access should fail because the repository normalises to camelCase.");
    }

    [Fact]
    public async Task StepOutputResolver_AgainstCamelCaseSavedOutput_ResolvesByEitherCasing()
    {
        // Arrange — save POCO with PascalCase, then attempt to resolve via @steps().
        var repo = new InMemoryOutputsRepository();
        var runStore = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        var ctx = new CoreExecutionContext { RunId = runId };
        var flow = MakeFlow();
        var step = new StepInstance("fetch", "Work") { RunId = runId };
        var result = new StepResult
        {
            Key = "fetch",
            Status = StepStatus.Succeeded,
            Result = new { OrderId = 42, CustomerName = "Alice" }
        };
        await repo.SaveStepOutputAsync(ctx, flow, step, result);

        var resolver = new StepOutputResolver(repo, runStore, runId, flow.Manifest.Steps);

        // Act
        var camelHit = await resolver.ResolveAsync("@steps('fetch').output.orderId");
        var pascalHit = await resolver.ResolveAsync("@steps('fetch').output.OrderId");

        // Assert — both casings reach the same camelCase record (issue #177): the manifest is
        // written in C# and naturally spells the path the way the POCO does.
        Assert.NotNull(camelHit);
        Assert.Equal(42, Assert.IsType<JsonElement>(camelHit).GetInt32());
        Assert.NotNull(pascalHit);
        Assert.Equal(42, Assert.IsType<JsonElement>(pascalHit).GetInt32());
    }

    [Fact]
    public async Task StepOutputResolver_RawJsonElementWithPascalCase_PreservesCasingAsWritten()
    {
        // Arrange — when the handler returns a raw JsonElement (already serialised),
        // the repository must NOT re-serialise (would corrupt the casing).
        // Verify the original PascalCase casing survives the round-trip.
        var repo = new InMemoryOutputsRepository();
        var runStore = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        var ctx = new CoreExecutionContext { RunId = runId };
        var flow = MakeFlow();
        var step = new StepInstance("fetch", "Work") { RunId = runId };
        var rawJson = JsonSerializer.Deserialize<JsonElement>(
            "{\"OrderId\":99,\"customerName\":\"Bob\"}");
        var result = new StepResult
        {
            Key = "fetch",
            Status = StepStatus.Succeeded,
            Result = rawJson
        };
        await repo.SaveStepOutputAsync(ctx, flow, step, result);

        var resolver = new StepOutputResolver(repo, runStore, runId, flow.Manifest.Steps);

        // Act — attempt both casings; raw JsonElement preserves what the handler emitted.
        var pascalHit = await resolver.ResolveAsync("@steps('fetch').output.OrderId");
        var camelHit = await resolver.ResolveAsync("@steps('fetch').output.customerName");

        // Assert — the original casing survives (no normalisation when input is JsonElement).
        Assert.NotNull(pascalHit);
        Assert.Equal(99, Assert.IsType<JsonElement>(pascalHit).GetInt32());
        Assert.NotNull(camelHit);
        Assert.Equal("Bob", Assert.IsType<JsonElement>(camelHit).GetString());
    }

    private static IFlowDefinition MakeFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { ["fetch"] = new StepMetadata { Type = "Work" } }
        });
        return flow;
    }
}
