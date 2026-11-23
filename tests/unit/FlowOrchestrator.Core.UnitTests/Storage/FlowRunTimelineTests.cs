using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// <see cref="FlowRunTimeline.Build"/> — the run timeline that replaced the persisted
/// <c>FlowEvents</c> log in 2.0, derived from the run record and its step attempts.
/// </summary>
public sealed class FlowRunTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RunId = Guid.NewGuid();

    private static FlowStepAttemptRecord Attempt(string key, int no, string status, int startSec, int? endSec, string? error = null) => new()
    {
        RunId = RunId,
        StepKey = key,
        Attempt = no,
        StepType = "Work",
        Status = status,
        ErrorMessage = error,
        StartedAt = T0.AddSeconds(startSec),
        CompletedAt = endSec is { } e ? T0.AddSeconds(e) : null
    };

    private static FlowStepRecord Step(string key, string status, params FlowStepAttemptRecord[] attempts) => new()
    {
        RunId = RunId,
        StepKey = key,
        StepType = "Work",
        Status = status,
        StartedAt = attempts.Length > 0 ? attempts[0].StartedAt : T0,
        CompletedAt = attempts.Length > 0 ? attempts[^1].CompletedAt : null,
        Attempts = attempts
    };

    private static FlowRunRecord Run(string status, int? endSec, params FlowStepRecord[] steps) => new()
    {
        Id = RunId,
        Status = status,
        TriggerKey = "manual",
        StartedAt = T0,
        CompletedAt = endSec is { } e ? T0.AddSeconds(e) : null,
        Steps = steps
    };

    private static string[] Types(IReadOnlyList<FlowEventRecord> timeline) => timeline.Select(e => e.Type).ToArray();

    [Fact]
    public void Completed_run_with_one_step_yields_started_completed_pairs_in_order()
    {
        // Arrange
        var run = Run("Succeeded", 5, Step("a", "Succeeded", Attempt("a", 1, "Succeeded", 1, 2)));

        // Act
        var timeline = FlowRunTimeline.Build(run);

        // Assert
        Assert.Equal(
            [FlowRunTimeline.RunStarted, FlowRunTimeline.StepStarted, FlowRunTimeline.StepCompleted, FlowRunTimeline.RunCompleted],
            Types(timeline));
        Assert.Equal([1L, 2, 3, 4], timeline.Select(e => e.Sequence));
        Assert.All(timeline, e => Assert.Equal(RunId, e.RunId));
        Assert.Null(timeline[0].StepKey);
        Assert.Equal("a", timeline[1].StepKey);
        Assert.Equal("Flow run started via trigger 'manual'.", timeline[0].Message);
        Assert.Equal("Run completed with status Succeeded.", timeline[3].Message);
    }

    [Fact]
    public void Each_attempt_contributes_its_own_entries_and_steps_interleave_by_time()
    {
        // Arrange — a fails at 2s and succeeds on retry at 6s; b runs in between.
        var run = Run(
            "Succeeded",
            10,
            Step("a", "Succeeded", Attempt("a", 1, "Failed", 1, 2, "boom"), Attempt("a", 2, "Succeeded", 5, 6)),
            Step("b", "Succeeded", Attempt("b", 1, "Succeeded", 3, 4)));

        // Act
        var timeline = FlowRunTimeline.Build(run);

        // Assert
        Assert.Equal(
            ["run.started", "step.started", "step.failed", "step.started", "step.completed", "step.started", "step.completed", "run.completed"],
            Types(timeline));
        Assert.Equal(["a", "a", "b", "b", "a", "a"], timeline.Skip(1).Take(6).Select(e => e.StepKey));
        Assert.Equal("boom", timeline[2].Message);
    }

    [Fact]
    public void Skipped_step_yields_a_single_skip_entry_carrying_the_recorded_reason()
    {
        // Arrange
        var reason = "When clause '@x > 1' evaluated to false (0 > 1).";
        var run = Run("Succeeded", 5, Step("s", "Skipped", Attempt("s", 1, "Skipped", 2, 2, reason)));

        // Act
        var timeline = FlowRunTimeline.Build(run);

        // Assert
        Assert.Equal(["run.started", "step.skipped", "run.completed"], Types(timeline));
        Assert.Equal(reason, timeline[1].Message);
    }

    [Fact]
    public void Repeated_skipped_attempts_of_one_step_are_reported_once()
    {
        // Arrange — the timeout sweep skipped the parked step, then its safety-net invocation hit the
        // entry gate long after the run closed and recorded another Skipped attempt.
        var run = Run(
            "TimedOut",
            10,
            Step("w", "Skipped", Attempt("w", 1, "Skipped", 1, 9, "Run is TimedOut."), Attempt("w", 2, "Skipped", 3600, 3600, "Run is TimedOut.")));

        // Act
        var timeline = FlowRunTimeline.Build(run);

        // Assert
        Assert.Equal(["run.started", "step.skipped", "run.completed"], Types(timeline));
        Assert.Equal(T0.AddSeconds(9), timeline[1].Timestamp);
    }

    [Fact]
    public void Pending_and_running_attempts_yield_step_pending_and_in_flight_attempts_only_started()
    {
        // Arrange — two poll iterations that returned Pending, then a third still executing; the run is open.
        var run = Run(
            "Running",
            null,
            Step(
                "poll",
                "Running",
                Attempt("poll", 1, "Pending", 1, 2),
                Attempt("poll", 2, "Pending", 3, 4),
                Attempt("poll", 3, "Running", 5, null)),
            Step("loop", "Running", Attempt("loop", 1, "Running", 6, 7)));

        // Act
        var timeline = FlowRunTimeline.Build(run);

        // Assert — no run.completed while the run is open.
        Assert.Equal(
            ["run.started", "step.started", "step.pending", "step.started", "step.pending", "step.started", "step.started", "step.pending"],
            Types(timeline));
        Assert.Contains("child steps", timeline[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Step_without_attempt_history_is_derived_from_the_step_row()
    {
        // Arrange
        var step = new FlowStepRecord
        {
            RunId = RunId,
            StepKey = "legacy",
            StepType = "Work",
            Status = "Failed",
            ErrorMessage = "nope",
            StartedAt = T0.AddSeconds(1),
            CompletedAt = T0.AddSeconds(2),
            Attempts = null
        };
        var run = Run("Failed", 3, step);

        // Act
        var timeline = FlowRunTimeline.Build(run);

        // Assert
        Assert.Equal(["run.started", "step.started", "step.failed", "run.completed"], Types(timeline));
        Assert.Equal("nope", timeline[2].Message);
    }

    [Fact]
    public void Timestamp_ties_keep_run_started_first_run_completed_last_and_started_before_outcome()
    {
        // Arrange — everything lands on the same instant (coarse clocks, synchronous in-memory runs).
        var run = Run("Succeeded", 0, Step("a", "Succeeded", Attempt("a", 1, "Succeeded", 0, 0)));

        // Act
        var timeline = FlowRunTimeline.Build(run);

        // Assert
        Assert.Equal(["run.started", "step.started", "step.completed", "run.completed"], Types(timeline));
    }

    [Fact]
    public void Run_without_steps_or_trigger_key_still_yields_run_entries()
    {
        // Arrange
        var run = new FlowRunRecord { Id = RunId, Status = "Cancelled", StartedAt = T0, CompletedAt = T0.AddSeconds(1), Steps = null };

        // Act
        var timeline = FlowRunTimeline.Build(run);

        // Assert
        Assert.Equal(["run.started", "run.completed"], Types(timeline));
        Assert.Equal("Flow run started.", timeline[0].Message);
        Assert.Equal("Run completed with status Cancelled.", timeline[1].Message);
    }

    [Fact]
    public void Null_run_throws()
    {
        // Arrange
        FlowRunRecord run = null!;

        // Act
        var ex = Record.Exception(() => FlowRunTimeline.Build(run));

        // Assert
        Assert.IsType<ArgumentNullException>(ex);
    }
}
