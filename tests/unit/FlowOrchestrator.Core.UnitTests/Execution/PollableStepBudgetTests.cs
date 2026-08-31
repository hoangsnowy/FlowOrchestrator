using System.Globalization;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Gap-fill coverage for <see cref="PollableStepHandler{TInput}"/>'s attempt and timeout budget:
/// the clamping rules on the configured values, the precedence between "condition matched" and
/// "budget exhausted", and what happens to the persisted poll state on the failure paths.
/// </summary>
/// <remarks>
/// The happy paths (poll disabled, condition not matched, match after the minimum attempts, plain
/// timeout, non-JSON body with a condition path) are already covered by
/// <c>PollableStepHandlerTests</c>; nothing here duplicates them. Elapsed time is controlled purely
/// by writing <c>PollStartedAtUtc</c> into the past, so no test sleeps or races the clock.
/// </remarks>
public sealed class PollableStepBudgetTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static IExecutionContext Context() => new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };

    private static string Ago(TimeSpan span) =>
        DateTimeOffset.UtcNow.Subtract(span).ToString("O", CultureInfo.InvariantCulture);

    private sealed class BudgetInput : IPollableInput
    {
        public bool PollEnabled { get; set; } = true;
        public int PollIntervalSeconds { get; set; } = 10;
        public int PollTimeoutSeconds { get; set; } = 300;
        public int PollMinAttempts { get; set; } = 1;
        public string? PollConditionPath { get; set; } = "status";
        public object? PollConditionEquals { get; set; } = "completed";
        public string? PollStartedAtUtc { get; set; }
        public int? PollAttempt { get; set; }
    }

    private sealed class BudgetFlow : IFlowDefinition
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new();
    }

    private sealed class BudgetStep(BudgetInput inputs) : IStepInstance<BudgetInput>
    {
        public Guid RunId { get; set; }
        public string? PrincipalId { get; set; }
        public object? TriggerData { get; set; }
        public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
        public string? JobId { get; set; }
        public DateTimeOffset ScheduledTime { get; set; }
        public string Type { get; set; } = "Pollable";
        public string Key => "poll";
        public BudgetInput Inputs { get; set; } = inputs;
        public int Index { get; set; }
        public bool ScopeMoveNext { get; set; }
    }

    private sealed class BudgetHandler(Func<(JsonElement Result, bool IsJson)> fetch) : PollableStepHandler<BudgetInput>
    {
        private readonly Func<(JsonElement Result, bool IsJson)> _fetch = fetch;

        /// <summary>Number of times <see cref="FetchAsync"/> was entered.</summary>
        public int FetchCount { get; private set; }

        protected override ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
            IExecutionContext ctx, IFlowDefinition flow, IStepInstance<BudgetInput> step)
        {
            FetchCount++;
            return ValueTask.FromResult(_fetch());
        }
    }

    private static async Task<StepResult<JsonElement>> RunAsync(BudgetHandler handler, BudgetInput input) =>
        Assert.IsType<StepResult<JsonElement>>(
            await handler.ExecuteAsync(Context(), new BudgetFlow(), new BudgetStep(input)));

    [Fact]
    public async Task TimeoutBelowInterval_isClampedUpToTheInterval()
    {
        // Arrange - a 30 s interval with a 5 s timeout is nonsensical; the base class clamps the
        // timeout up so at least one full interval is always allowed.
        var input = new BudgetInput
        {
            PollIntervalSeconds = 30,
            PollTimeoutSeconds = 5,
            PollStartedAtUtc = Ago(TimeSpan.FromSeconds(45))
        };
        var handler = new BudgetHandler(() => (Json("{\"status\":\"processing\"}"), true));

        // Act
        var result = await RunAsync(handler, input);

        // Assert - the reported budget is the clamped 30 s, not the configured 5 s.
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("Polling timed out after 30 seconds.", result.FailedReason ?? string.Empty);
    }

    [Fact]
    public async Task NonPositiveIntervalAndMinAttempts_areClampedToOne()
    {
        // Arrange
        var input = new BudgetInput
        {
            PollIntervalSeconds = 0,
            PollMinAttempts = 0,
            PollTimeoutSeconds = 600
        };
        var handler = new BudgetHandler(() => (Json("{\"status\":\"processing\"}"), true));

        // Act
        var result = await RunAsync(handler, input);

        // Assert - a zero interval reschedules after one second rather than immediately.
        Assert.Equal(StepStatus.Pending, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(1), result.DelayNextStep);
    }

    [Fact]
    public async Task MinAttemptsClampedToOne_letsTheFirstMatchingResponseSucceed()
    {
        // Arrange - PollMinAttempts = 0 must not mean "never evaluate"; it clamps to one attempt.
        var input = new BudgetInput { PollMinAttempts = 0, PollTimeoutSeconds = 600 };
        var handler = new BudgetHandler(() => (Json("{\"status\":\"completed\"}"), true));

        // Act
        var result = await RunAsync(handler, input);

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Null(input.PollAttempt);
        Assert.Null(input.PollStartedAtUtc);
    }

    [Fact]
    public async Task IntervalOfIntMaxValue_doesNotOverflowTheRescheduleDelay()
    {
        // Arrange - an absurd interval must clamp the timeout up to the same value and still produce
        // a representable TimeSpan rather than throwing out of the handler.
        var input = new BudgetInput { PollIntervalSeconds = int.MaxValue, PollTimeoutSeconds = 60 };
        var handler = new BudgetHandler(() => (Json("{\"status\":\"processing\"}"), true));

        // Act
        var result = await RunAsync(handler, input);

        // Assert
        Assert.Equal(StepStatus.Pending, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(int.MaxValue), result.DelayNextStep);
    }

    [Fact]
    public async Task ConditionMatchedOnTheAttemptThatAlsoExhaustsTheBudget_succeeds()
    {
        // Arrange - the match check runs before the elapsed-time check, so a response that arrives
        // exactly as the budget runs out is honoured instead of being reported as a timeout.
        var input = new BudgetInput
        {
            PollIntervalSeconds = 5,
            PollTimeoutSeconds = 10,
            PollAttempt = 3,
            PollStartedAtUtc = Ago(TimeSpan.FromMinutes(5))
        };
        var handler = new BudgetHandler(() => (Json("{\"status\":\"completed\"}"), true));

        // Act
        var result = await RunAsync(handler, input);

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Null(result.FailedReason);
    }

    [Fact]
    public async Task CorruptedPollStartedAtUtc_restartsTheBudgetWindowInsteadOfFailing()
    {
        // Arrange - a persisted value that no longer round-trips (manual edit, truncated column,
        // storage migration). The base class cannot honour it, so it stamps a fresh start time.
        var input = new BudgetInput
        {
            PollIntervalSeconds = 5,
            PollTimeoutSeconds = 10,
            PollAttempt = 7,
            PollStartedAtUtc = "not-a-timestamp"
        };
        var handler = new BudgetHandler(() => (Json("{\"status\":\"processing\"}"), true));

        // Act
        var result = await RunAsync(handler, input);

        // Assert - pins the consequence: the elapsed budget silently restarts from now, so a step
        // whose start stamp keeps getting corrupted can poll indefinitely. The attempt counter is
        // preserved, which is the only remaining evidence of the earlier attempts.
        Assert.Equal(StepStatus.Pending, result.Status);
        Assert.Equal(8, input.PollAttempt);
        Assert.NotNull(input.PollStartedAtUtc);
        Assert.True(DateTimeOffset.TryParse(
                input.PollStartedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            "a fresh, parseable start stamp must be written");
    }

    [Fact]
    public async Task FetchThatThrows_propagatesAndDoesNotConsumeAnAttempt()
    {
        // Arrange - the attempt counter is incremented only after a successful fetch, so a transient
        // outage in the polled system does not eat into the attempt budget.
        var input = new BudgetInput { PollAttempt = 2, PollStartedAtUtc = Ago(TimeSpan.FromSeconds(20)) };
        var handler = new BudgetHandler(() => throw new InvalidOperationException("endpoint down"));

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.ExecuteAsync(Context(), new BudgetFlow(), new BudgetStep(input)).AsTask());

        // Assert
        Assert.Equal("endpoint down", ex.Message);
        Assert.Equal(2, input.PollAttempt);
    }

    [Fact]
    public async Task PollDisabled_shortCircuitsAheadOfTheNonJsonGuard()
    {
        // Arrange - a non-JSON body with a condition path is a configuration error, but only while
        // polling is on. With PollEnabled = false the handler is a plain one-shot fetch.
        var input = new BudgetInput { PollEnabled = false, PollConditionPath = "status" };
        var handler = new BudgetHandler(() => (Json("\"plain text\""), false));

        // Act
        var result = await RunAsync(handler, input);

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Null(result.FailedReason);
        Assert.Null(input.PollAttempt);
    }

    [Fact]
    public async Task ExhaustedBudget_clearsPollStateSoARetryStartsAFreshWindow()
    {
        // Arrange
        var input = new BudgetInput
        {
            PollIntervalSeconds = 5,
            PollTimeoutSeconds = 10,
            PollAttempt = 4,
            PollStartedAtUtc = Ago(TimeSpan.FromMinutes(1))
        };
        var handler = new BudgetHandler(() => (Json("{\"status\":\"processing\"}"), true));

        // Act
        var failed = await RunAsync(handler, input);
        var afterRetry = await RunAsync(handler, input);

        // Assert - the timeout resets the persisted budget, so a manual retry of the step polls
        // again from scratch rather than failing instantly forever.
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Equal(StepStatus.Pending, afterRetry.Status);
        Assert.Equal(1, input.PollAttempt);
    }

    // -- Engine-level: run budget versus poll budget ----------------------------------------

    private static IFlowDefinition PollingFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["poll"] = new StepMetadata { Type = "Pollable" },
                ["after_poll"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["poll"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    [Fact]
    public async Task RunLevelTimeout_outranksThePollBudget_andTheStepLandsSkippedNotFailed()
    {
        // Arrange - the step still has plenty of poll budget left (it keeps returning Pending), but
        // the run's own deadline lapses first.
        var harness = new LoopBarrierEngineHarness(
            PollingFlow(),
            key => key == "poll"
                ? new StepResult { Key = key, Status = StepStatus.Pending, DelayNextStep = TimeSpan.FromSeconds(5) }
                : new StepResult { Key = key, Status = StepStatus.Succeeded });

        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("poll");
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act
        await harness.DrainAsync();

        // Assert - the run-level verdict wins and is attributed as such: the step is Skipped with the
        // run's reason, never Failed with a polling-timeout reason.
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "poll"));
        Assert.Equal("Run is TimedOut.", await harness.StepReasonAsync(runId, "poll"));
        Assert.DoesNotContain("after_poll", harness.Enqueued);
    }

    [Fact]
    public async Task PollBudgetExhaustion_whileTheRunDeadlineIsStillAlive_failsOnlyTheStep()
    {
        // Arrange - the mirror image: the poll budget runs out first, so the handler's own Failed
        // result decides the step outcome and the run fails through the ordinary classifier.
        var harness = new LoopBarrierEngineHarness(
            PollingFlow(),
            key => key == "poll"
                ? new StepResult
                {
                    Key = key,
                    Status = StepStatus.Failed,
                    FailedReason = "Polling timed out after 10 seconds."
                }
                : new StepResult { Key = key, Status = StepStatus.Succeeded });

        var runId = await harness.TriggerAsync();

        // Act
        await harness.DrainAsync();

        // Assert
        Assert.Equal("Failed", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Failed.ToString(), await harness.StepStatusAsync(runId, "poll"));
        Assert.Equal("Polling timed out after 10 seconds.", await harness.StepReasonAsync(runId, "poll"));
    }
}
