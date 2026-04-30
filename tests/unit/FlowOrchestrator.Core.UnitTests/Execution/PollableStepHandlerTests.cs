using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Core.Tests.Execution;

public class PollableStepHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_PollDisabled_ReturnsSucceeded()
    {
        // Arrange
        var input = new TestPollableInput { PollEnabled = false };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (ParseJson("{\"status\":\"processing\"}"), true));

        // Act
        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);

        // Assert
        var typedResult = Assert.IsType<StepResult<JsonElement>>(result);
        Assert.Equal(StepStatus.Succeeded, typedResult.Status);
        Assert.Null(typedResult.DelayNextStep);
        Assert.Null(input.PollAttempt);
        Assert.Null(input.PollStartedAtUtc);
    }

    [Fact]
    public async Task ExecuteAsync_ConditionNotMatched_ReturnsPending()
    {
        // Arrange
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

        // Act
        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);

        // Assert
        var typedResult = Assert.IsType<StepResult<JsonElement>>(result);
        Assert.Equal(StepStatus.Pending, typedResult.Status);
        Assert.Equal(TimeSpan.FromSeconds(7), typedResult.DelayNextStep);
        Assert.Equal(1, input.PollAttempt);
        Assert.False(string.IsNullOrWhiteSpace(input.PollStartedAtUtc));
    }

    [Fact]
    public async Task ExecuteAsync_MatchAfterMinimumAttempts_ReturnsSucceededAndResetsState()
    {
        // Arrange
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

        // Act
        var first = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);
        var second = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);

        // Assert
        var firstResult = Assert.IsType<StepResult<JsonElement>>(first);
        Assert.Equal(StepStatus.Pending, firstResult.Status);
        Assert.Equal(TimeSpan.FromSeconds(3), firstResult.DelayNextStep);

        var secondResult = Assert.IsType<StepResult<JsonElement>>(second);
        Assert.Equal(StepStatus.Succeeded, secondResult.Status);
        Assert.Null(secondResult.DelayNextStep);
        Assert.Null(input.PollAttempt);
        Assert.Null(input.PollStartedAtUtc);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutExceeded_ReturnsFailedAndResetsState()
    {
        // Arrange
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

        // Act
        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);

        // Assert
        var typedResult = Assert.IsType<StepResult<JsonElement>>(result);
        Assert.Equal(StepStatus.Failed, typedResult.Status);
        Assert.Contains("Polling timed out after 10 seconds.", typedResult.FailedReason ?? string.Empty);
        Assert.Null(input.PollAttempt);
        Assert.Null(input.PollStartedAtUtc);
    }

    [Fact]
    public async Task ExecuteAsync_NonJsonWithConditionPath_ReturnsFailedAndResetsState()
    {
        // Arrange
        var input = new TestPollableInput
        {
            PollEnabled = true,
            PollConditionPath = "status",
            PollStartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-15).ToString("O"),
            PollAttempt = 4
        };
        var step = new TestStepInstance("step1", "Pollable", input);
        var handler = new TestPollableHandler(() => (JsonSerializer.SerializeToElement("plain-response"), false));

        // Act
        var result = await handler.ExecuteAsync(CreateContext(), CreateFlow(), step);

        // Assert
        var typedResult = Assert.IsType<StepResult<JsonElement>>(result);
        Assert.Equal(StepStatus.Failed, typedResult.Status);
        Assert.Contains("requires a JSON response body", typedResult.FailedReason ?? string.Empty);
        Assert.Null(input.PollAttempt);
        Assert.Null(input.PollStartedAtUtc);
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
        public string? JobId { get; set; }
        public DateTimeOffset ScheduledTime { get; set; }
        public string Type { get; set; }
        public string Key { get; }
        public TestPollableInput Inputs { get; set; }
        public int Index { get; set; }
        public bool ScopeMoveNext { get; set; }
    }
}
