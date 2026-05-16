using FlowOrchestrator.Core.Observability;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Observability;

/// <summary>
/// Tests for <see cref="EngineLogScope.Begin"/> — the helper that opens the per-step
/// logger scope carrying <c>RunId</c> / <c>FlowId</c> / <c>StepKey</c> / <c>Attempt</c>.
/// Guards the CR/LF sanitisation introduced as part of the CodeQL log-forging sweep
/// (CWE-117); a step key crafted with embedded newlines must NOT be able to inject
/// fake log lines into the scope state.
/// </summary>
public sealed class EngineLogScopeTests
{
    [Fact]
    public void Begin_WhenLoggerIsNull_ReturnsNullWithoutThrowing()
    {
        // Arrange
        ILogger? logger = null;

        // Act
        var scope = EngineLogScope.Begin(logger, Guid.NewGuid(), Guid.NewGuid(), "step1");

        // Assert
        Assert.Null(scope);
    }

    [Fact]
    public void Begin_PopulatesRunIdAndFlowIdAlways()
    {
        // Arrange
        var logger = new RecordingLogger();
        var runId = Guid.NewGuid();
        var flowId = Guid.NewGuid();

        // Act
        using var _ = EngineLogScope.Begin(logger, runId, flowId);

        // Assert
        var scope = Assert.Single(logger.Scopes);
        Assert.Equal(runId, scope["RunId"]);
        Assert.Equal(flowId, scope["FlowId"]);
        Assert.False(scope.ContainsKey("StepKey"));
        Assert.False(scope.ContainsKey("Attempt"));
    }

    [Fact]
    public void Begin_WhenStepKeyContainsCarriageReturn_ReplacesWithUnderscore()
    {
        // Arrange — a step key with a forged log-line break must NOT propagate as-is.
        var logger = new RecordingLogger();

        // Act
        using var _ = EngineLogScope.Begin(
            logger,
            Guid.NewGuid(),
            Guid.NewGuid(),
            stepKey: "evil\rINJECTED");

        // Assert — the CR is replaced with '_' so no log provider can be tricked into
        // emitting a second line. The injection content is preserved (audit) but neutered.
        var scope = Assert.Single(logger.Scopes);
        Assert.Equal("evil_INJECTED", scope["StepKey"]);
    }

    [Fact]
    public void Begin_WhenStepKeyContainsLineFeed_ReplacesWithUnderscore()
    {
        // Arrange
        var logger = new RecordingLogger();

        // Act
        using var _ = EngineLogScope.Begin(
            logger,
            Guid.NewGuid(),
            Guid.NewGuid(),
            stepKey: "evil\nINJECTED");

        // Assert
        var scope = Assert.Single(logger.Scopes);
        Assert.Equal("evil_INJECTED", scope["StepKey"]);
    }

    [Fact]
    public void Begin_WhenStepKeyContainsCrLf_ReplacesBothCharacters()
    {
        // Arrange — Windows-style line ending: both CR and LF must be sanitised.
        var logger = new RecordingLogger();

        // Act
        using var _ = EngineLogScope.Begin(
            logger,
            Guid.NewGuid(),
            Guid.NewGuid(),
            stepKey: "first\r\nsecond");

        // Assert
        var scope = Assert.Single(logger.Scopes);
        Assert.Equal("first__second", scope["StepKey"]);
    }

    [Fact]
    public void Begin_WhenStepKeyIsNull_OmitsStepKeyFromScope()
    {
        // Arrange
        var logger = new RecordingLogger();

        // Act
        using var _ = EngineLogScope.Begin(logger, Guid.NewGuid(), Guid.NewGuid(), stepKey: null);

        // Assert — null is treated as "not set"; StepKey must not appear in the scope state.
        var scope = Assert.Single(logger.Scopes);
        Assert.False(scope.ContainsKey("StepKey"));
    }

    [Fact]
    public void Begin_WhenStepKeyIsEmpty_OmitsStepKeyFromScope()
    {
        // Arrange
        var logger = new RecordingLogger();

        // Act
        using var _ = EngineLogScope.Begin(logger, Guid.NewGuid(), Guid.NewGuid(), stepKey: "");

        // Assert — empty string is treated as "not set" per the existing IsNullOrEmpty guard.
        var scope = Assert.Single(logger.Scopes);
        Assert.False(scope.ContainsKey("StepKey"));
    }

    [Fact]
    public void Begin_WhenAttemptIsSet_PopulatesAttemptInScope()
    {
        // Arrange
        var logger = new RecordingLogger();

        // Act
        using var _ = EngineLogScope.Begin(
            logger,
            Guid.NewGuid(),
            Guid.NewGuid(),
            stepKey: "step1",
            attempt: 3);

        // Assert
        var scope = Assert.Single(logger.Scopes);
        Assert.Equal(3, scope["Attempt"]);
    }

    /// <summary>Logger substitute that captures every BeginScope state for inspection.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                Scopes.Add(kvps.ToDictionary(k => k.Key, v => v.Value));
            }
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // No-op; these tests assert on scope state, not log lines.
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
