using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// Guards the source-compatibility contract for <see cref="IFlowRunControlStore.ExtendDeadlineAsync"/>:
/// it ships as a default interface method so existing custom control-store implementations that predate
/// it continue to compile, degrading retry to its previous no-op behaviour rather than failing to build.
/// </summary>
public sealed class FlowRunControlStoreDefaultMethodTests
{
    /// <summary>
    /// A minimal custom control store that implements every required member but deliberately does NOT
    /// override <see cref="IFlowRunControlStore.ExtendDeadlineAsync"/>. That it compiles at all is half
    /// the assertion; the test verifies the inherited default returns <see langword="false"/>.
    /// </summary>
    private sealed class LegacyControlStore : IFlowRunControlStore
    {
        public Task ConfigureRunAsync(Guid runId, Guid flowId, string triggerKey, string? idempotencyKey, DateTimeOffset? timeoutAtUtc)
            => Task.CompletedTask;

        public Task<FlowRunControlRecord?> GetRunControlAsync(Guid runId)
            => Task.FromResult<FlowRunControlRecord?>(null);

        public Task<bool> RequestCancelAsync(Guid runId, string? reason) => Task.FromResult(false);

        public Task<bool> MarkTimedOutAsync(Guid runId, string? reason) => Task.FromResult(false);

        public Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey)
            => Task.FromResult<Guid?>(null);

        public Task<bool> TryRegisterIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task ExtendDeadlineAsync_DefaultInterfaceMethod_ReturnsFalse()
    {
        // Arrange
        IFlowRunControlStore store = new LegacyControlStore();

        // Act
        var result = await store.ExtendDeadlineAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5));

        // Assert
        Assert.False(result);
    }
}
