using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Boundary-value tests for <see cref="PollableStepHandler{TInput}"/> covering
/// clamping behaviour for <c>PollMinAttempts</c>, <c>PollIntervalSeconds</c>, and
/// <c>PollTimeoutSeconds</c>, plus condition-path evaluation against non-object payloads.
/// Complements the primary <c>PollableStepHandlerTests</c> suite (Section G6 expression
/// edges, plus boundary numerics).
/// </summary>
public sealed class PollableStepHandlerBoundaryTests
{
    [Fact]
    public async Task ExecuteAsync_PollMinAttemptsZero_ClampsToOneAndAcceptsFirstMatch()
    {
        // Arrange — minAttempts of 0 should be clamped to 1 by the handler;
        // a single matched fetch should therefore succeed without rescheduling.
        var input = new TestPollableInput
        {
            PollEnabled = true,
            PollIntervalSeconds = 5,
            PollTimeoutSeconds = 60,
            PollMinAttempts = 0,
            PollConditionPath = "status",
            PollConditionEquals = "completed"
        };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (ParseJson("{\"status\":\"completed\"}"), true));

        // Act
        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);

        // Assert
        var typed = Assert.IsType<StepResult<JsonElement>>(result);
        Assert.Equal(StepStatus.Succeeded, typed.Status);
        Assert.Null(typed.DelayNextStep);
        Assert.Null(input.PollAttempt);
    }

    [Fact]
    public async Task ExecuteAsync_NegativeIntervalSeconds_ClampsToOneSecond()
    {
        // Arrange — a negative interval value would otherwise produce a negative TimeSpan.
        // The handler must clamp to a minimum of 1 second.
        var input = new TestPollableInput
        {
            PollEnabled = true,
            PollIntervalSeconds = -5,
            PollTimeoutSeconds = 60,
            PollConditionPath = "status",
            PollConditionEquals = "done"
        };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (ParseJson("{\"status\":\"processing\"}"), true));

        // Act
        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);

        // Assert
        var typed = Assert.IsType<StepResult<JsonElement>>(result);
        Assert.Equal(StepStatus.Pending, typed.Status);
        Assert.Equal(TimeSpan.FromSeconds(1), typed.DelayNextStep);
    }

    [Fact]
    public async Task ExecuteAsync_ConditionPathAgainstScalarRoot_DoesNotMatchAndReturnsPending()
    {
        // Arrange — handler receives a JSON scalar (number) but condition path expects an object.
        // Resolution must fail gracefully (treat as not-matched) rather than throwing.
        var input = new TestPollableInput
        {
            PollEnabled = true,
            PollIntervalSeconds = 3,
            PollTimeoutSeconds = 60,
            PollConditionPath = "status",
            PollConditionEquals = "completed"
        };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (JsonSerializer.SerializeToElement(42), true));

        // Act
        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);

        // Assert
        var typed = Assert.IsType<StepResult<JsonElement>>(result);
        Assert.Equal(StepStatus.Pending, typed.Status);
        Assert.Equal(1, input.PollAttempt);
    }

    private static IExecutionContext CreateContext() =>
        new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };

    private static IFlowDefinition CreateFlow() => new TestFlowDefinition();

    private static JsonElement ParseJson(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    private sealed class TestPollableHandler : PollableStepHandler<TestPollableInput>
    {
        private readonly Func<(JsonElement, bool)> _fetch;

        public TestPollableHandler(Func<(JsonElement, bool)> fetch)
        {
            _fetch = fetch;
        }

        protected override ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
            IExecutionContext ctx, IFlowDefinition flow, IStepInstance<TestPollableInput> step) =>
            ValueTask.FromResult(_fetch());
    }

    private sealed class TestPollableInput : IPollableInput
    {
        public bool PollEnabled { get; set; }
        public int PollIntervalSeconds { get; set; } = 10;
        public int PollTimeoutSeconds { get; set; } = 300;
        public int PollMinAttempts { get; set; } = 1;
        public string? PollConditionPath { get; set; }
        public object? PollConditionEquals { get; set; }
        public string? PollStartedAtUtc { get; set; }
        public int? PollAttempt { get; set; }
    }

    private sealed class TestFlowDefinition : IFlowDefinition
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new();
    }

    private sealed class TestStepInstance : IStepInstance<TestPollableInput>
    {
        public TestStepInstance(string key, string type, TestPollableInput inputs)
        {
            Key = key;
            Type = type;
            Inputs = inputs;
        }

        public Guid RunId { get; set; }
        public string? PrincipalId { get; set; }
        public object? TriggerData { get; set; }
        public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
        public string? JobId { get; set; }
        public DateTimeOffset ScheduledTime { get; set; }
        public string Type { get; set; }
        public string Key { get; }
        public TestPollableInput Inputs { get; set; }
        public int Index { get; set; }
        public bool ScopeMoveNext { get; set; }
    }
}
