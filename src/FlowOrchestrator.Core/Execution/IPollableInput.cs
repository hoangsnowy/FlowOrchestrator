namespace FlowOrchestrator.Core.Execution;

public interface IPollableInput
{
    bool PollEnabled { get; }
    int PollIntervalSeconds { get; }
    int PollTimeoutSeconds { get; }
    int PollMinAttempts { get; }
    string? PollConditionPath { get; }
    object? PollConditionEquals { get; }

    string? PollStartedAtUtc { get; set; }
    int? PollAttempt { get; set; }
}
