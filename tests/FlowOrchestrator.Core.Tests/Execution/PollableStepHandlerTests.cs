using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Execution;

public class PollableStepHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_PollDisabled_ReturnsSucceeded()
    {
        var input = new TestPollableInput { PollEnabled = false };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (ParseJson("{\"status\":\"processing\"}"), true));

        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);
        var typedResult = result.Should().BeOfType<StepResult<JsonElement>>().Subject;

        typedResult.Status.Should().Be(StepStatus.Succeeded);
        typedResult.DelayNextStep.Should().BeNull();
        input.PollAttempt.Should().BeNull();
        input.PollStartedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ConditionNotMatched_ReturnsPending()
    {
        var input = new TestPollableInput
        {
            PollEnabled = true,
            PollIntervalSeconds = 7,
            PollTimeoutSeconds = 60,
            PollConditionPath = "status",
            PollConditionEquals = "completed"
        };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (ParseJson("{\"status\":\"processing\"}"), true));

        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);
        var typedResult = result.Should().BeOfType<StepResult<JsonElement>>().Subject;

        typedResult.Status.Should().Be(StepStatus.Pending);
        typedResult.DelayNextStep.Should().Be(TimeSpan.FromSeconds(7));
        input.PollAttempt.Should().Be(1);
        input.PollStartedAtUtc.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_MatchAfterMinimumAttempts_ReturnsSucceededAndResetsState()
    {
        var input = new TestPollableInput
        {
            PollEnabled = true,
            PollIntervalSeconds = 3,
            PollTimeoutSeconds = 120,
            PollMinAttempts = 2,
            PollConditionPath = "status",
            PollConditionEquals = "completed"
        };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (ParseJson("{\"status\":\"completed\"}"), true));

        var first = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);
        var firstResult = first.Should().BeOfType<StepResult<JsonElement>>().Subject;
        firstResult.Status.Should().Be(StepStatus.Pending);
        firstResult.DelayNextStep.Should().Be(TimeSpan.FromSeconds(3));
        input.PollAttempt.Should().Be(1);
        input.PollStartedAtUtc.Should().NotBeNullOrWhiteSpace();

        var second = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);
        var secondResult = second.Should().BeOfType<StepResult<JsonElement>>().Subject;
        secondResult.Status.Should().Be(StepStatus.Succeeded);
        secondResult.DelayNextStep.Should().BeNull();
        input.PollAttempt.Should().BeNull();
        input.PollStartedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutExceeded_ReturnsFailedAndResetsState()
    {
        var input = new TestPollableInput
        {
            PollEnabled = true,
            PollIntervalSeconds = 3,
            PollTimeoutSeconds = 10,
            PollConditionPath = "status",
            PollConditionEquals = "completed",
            PollStartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30).ToString("O"),
            PollAttempt = 2
        };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (ParseJson("{\"status\":\"processing\"}"), true));

        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);
        var typedResult = result.Should().BeOfType<StepResult<JsonElement>>().Subject;

        typedResult.Status.Should().Be(StepStatus.Failed);
        typedResult.FailedReason.Should().Contain("Polling timed out after 10 seconds.");
        input.PollAttempt.Should().BeNull();
        input.PollStartedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_NonJsonWithConditionPath_ReturnsFailedAndResetsState()
    {
        var input = new TestPollableInput
        {
            PollEnabled = true,
            PollConditionPath = "status",
            PollStartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-15).ToString("O"),
            PollAttempt = 4
        };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (JsonSerializer.SerializeToElement("plain-response"), false));

        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);
        var typedResult = result.Should().BeOfType<StepResult<JsonElement>>().Subject;

        typedResult.Status.Should().Be(StepStatus.Failed);
        typedResult.FailedReason.Should().Contain("requires a JSON response body");
        input.PollAttempt.Should().BeNull();
        input.PollStartedAtUtc.Should().BeNull();
    }

    private static IExecutionContext CreateContext()
    {
        return new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
    }

    private static IFlowDefinition CreateFlow()
    {
        return new TestFlowDefinition();
    }

    private static JsonElement ParseJson(string json)
    {
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private sealed class TestPollableHandler : PollableStepHandler<TestPollableInput>
    {
        private readonly Func<(JsonElement Result, bool IsJson)> _fetch;

        public TestPollableHandler(Func<(JsonElement Result, bool IsJson)> fetch)
        {
            _fetch = fetch;
        }

        protected override ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
            IExecutionContext ctx, IFlowDefinition flow, IStepInstance<TestPollableInput> step)
        {
            return ValueTask.FromResult(_fetch());
        }
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
        public DateTimeOffset ScheduledTime { get; set; }
        public string Type { get; set; }
        public string Key { get; }
        public TestPollableInput Inputs { get; set; }
        public int Index { get; set; }
        public bool ScopeMoveNext { get; set; }
    }
}
