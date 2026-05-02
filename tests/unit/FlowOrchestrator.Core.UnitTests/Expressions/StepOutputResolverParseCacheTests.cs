using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Expressions;

/// <summary>
/// Concurrency and behavioural correctness for the process-wide
/// <see cref="StepOutputResolver"/> parse cache added in v1.20.0. The cache is
/// a static <c>ConcurrentDictionary</c> shared across every resolver instance
/// in the process, so its safety under contention and consistency across
/// instances is part of the engine's correctness contract.
/// </summary>
public sealed class StepOutputResolverParseCacheTests
{
    [Fact]
    public async Task ParallelResolveOfSameNewExpression_AllReturnSameOutput_NoExceptions()
    {
        // Arrange — 64 tasks, each with its OWN resolver instance, all racing
        // to populate the static process-wide parse cache for an expression
        // nobody has seen before. The parse cache (ConcurrentDictionary) is
        // the contended resource; per-instance state (output cache, run-detail
        // cache) is not — that matches the production usage where each step
        // execution has a fresh resolver.
        var stepKey = $"step_{Guid.NewGuid():N}";
        var steps = new StepCollection
        {
            [stepKey] = new StepMetadata { Type = "noop" }
        };
        var outputsRepo = Substitute.For<IOutputsRepository>();
        outputsRepo.GetStepOutputAsync(Arg.Any<Guid>(), Arg.Any<string>()).Returns("hello");
        var runStore = Substitute.For<IFlowRunStore>();
        var runId = Guid.NewGuid();
        var expression = $"@steps('{stepKey}').output";
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var tasks = Enumerable.Range(0, 64).Select(async _ =>
        {
            var resolver = new StepOutputResolver(outputsRepo, runStore, runId, steps);
            await startGate.Task;
            return await resolver.ResolveAsync(expression);
        }).ToArray();
        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert
        foreach (var r in results)
        {
            Assert.Equal("hello", r?.ToString());
        }
    }

    [Fact]
    public async Task TwoResolverInstances_SameProcess_SeeIdenticalParseResultForSameExpression()
    {
        // Arrange — the cache is process-wide; a fresh resolver should benefit
        // from a previous instance's parse work.
        var stepKey = $"step_{Guid.NewGuid():N}";
        var steps = new StepCollection
        {
            [stepKey] = new StepMetadata { Type = "noop" }
        };
        var outputsRepo = Substitute.For<IOutputsRepository>();
        outputsRepo.GetStepOutputAsync(Arg.Any<Guid>(), Arg.Any<string>()).Returns("payload");
        var runStore = Substitute.For<IFlowRunStore>();
        var runId = Guid.NewGuid();
        var resolverA = new StepOutputResolver(outputsRepo, runStore, runId, steps);
        var resolverB = new StepOutputResolver(outputsRepo, runStore, runId, steps);
        var expression = $"@steps('{stepKey}').output";

        // Act
        var resultA = await resolverA.ResolveAsync(expression);
        var resultB = await resolverB.ResolveAsync(expression);

        // Assert
        Assert.Equal("payload", resultA?.ToString());
        Assert.Equal("payload", resultB?.ToString());
    }

    [Fact]
    public async Task NonStepExpression_PassesThrough_AndIsCachedAsMiss()
    {
        // Arrange — a literal that does not match the @steps() pattern should
        // be returned unchanged. Cached negatively so subsequent calls also
        // skip the regex.
        var steps = new StepCollection();
        var outputsRepo = Substitute.For<IOutputsRepository>();
        var runStore = Substitute.For<IFlowRunStore>();
        var resolver = new StepOutputResolver(outputsRepo, runStore, Guid.NewGuid(), steps);
        var expression = "ProductionOrderId-12345";

        // Act
        var first = await resolver.ResolveAsync(expression);
        var second = await resolver.ResolveAsync(expression);

        // Assert
        Assert.Equal(expression, first);
        Assert.Equal(expression, second);
    }

    [Fact]
    public async Task ExpressionWithLeadingWhitespace_ResolvesIdenticallyToTrimmed()
    {
        // Arrange — the resolver trims its input before parsing; the cache key
        // is the trimmed form so whitespace variants share a single entry.
        var stepKey = $"step_{Guid.NewGuid():N}";
        var steps = new StepCollection
        {
            [stepKey] = new StepMetadata { Type = "noop" }
        };
        var outputsRepo = Substitute.For<IOutputsRepository>();
        outputsRepo.GetStepOutputAsync(Arg.Any<Guid>(), Arg.Any<string>()).Returns("body");
        var runStore = Substitute.For<IFlowRunStore>();
        var runId = Guid.NewGuid();
        var resolver = new StepOutputResolver(outputsRepo, runStore, runId, steps);

        // Act
        var trimmed = await resolver.ResolveAsync($"@steps('{stepKey}').output");
        var spaced = await resolver.ResolveAsync($"   @steps('{stepKey}').output  ");

        // Assert
        Assert.Equal(trimmed?.ToString(), spaced?.ToString());
    }
}
