using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Regression tests for known-issue #7 in qa-agent.md — execute-time dedup in
/// <see cref="ServiceBusFlowProcessorHostedService"/> when Aspire-emulator broadcast
/// causes the same dispatched step message to land on every subscription on the topic.
/// </summary>
/// <remarks>
/// The hosted service exposes <c>TryStartProcessing</c> / <c>EndProcessing</c> as internal
/// seams (`InternalsVisibleTo` from the production csproj). Tests drive these directly to
/// pin the dedup contract — first arrival wins, subsequent concurrent deliveries are dropped
/// silently, and the slot reopens after `EndProcessing` so genuine retries can run.
/// </remarks>
public class ServiceBusFlowProcessorDedupTests
{
    private const string FakeConn =
        "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAAA";

    private static ServiceBusFlowProcessorHostedService CreateSut() => new(
        client: new ServiceBusClient(FakeConn),
        options: new ServiceBusRuntimeOptions { ConnectionString = FakeConn },
        topology: new ServiceBusTopologyManager(
            new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(FakeConn),
            new ServiceBusRuntimeOptions { ConnectionString = FakeConn },
            NullLogger<ServiceBusTopologyManager>.Instance),
        repository: Substitute.For<IFlowRepository>(),
        flowStore: Substitute.For<IFlowStore>(),
        scopeFactory: Substitute.For<IServiceScopeFactory>(),
        logger: NullLogger<ServiceBusFlowProcessorHostedService>.Instance);

    [Fact]
    public void TryStartProcessing_FirstCall_ReturnsTrue()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var first = sut.TryStartProcessing(Guid.NewGuid(), "step1");

        // Assert
        Assert.True(first);
    }

    [Fact]
    public void TryStartProcessing_SecondConcurrentCall_ReturnsFalse()
    {
        // Arrange
        var sut = CreateSut();
        var runId = Guid.NewGuid();
        var first = sut.TryStartProcessing(runId, "step1");

        // Act — second arrival for the same (runId, stepKey) while the first is still
        // in-flight (no EndProcessing yet) must be reported as a duplicate.
        var second = sut.TryStartProcessing(runId, "step1");

        // Assert
        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void TryStartProcessing_AfterEndProcessing_ReturnsTrueAgain()
    {
        // Arrange — represents a genuine retry: the original delivery completed, then a
        // separate dispatch for the same step (e.g., poll re-schedule, runtime retry) arrives.
        var sut = CreateSut();
        var runId = Guid.NewGuid();
        sut.TryStartProcessing(runId, "step1");
        sut.EndProcessing(runId, "step1");

        // Act
        var retry = sut.TryStartProcessing(runId, "step1");

        // Assert
        Assert.True(retry);
    }

    [Fact]
    public void TryStartProcessing_DifferentStepKeysOnSameRun_AllReturnTrue()
    {
        // Arrange — broadcast scenario for a multi-step flow: every subscription receives
        // every step message, but only one (per step) wins. Different step keys must NOT
        // alias to one another.
        var sut = CreateSut();
        var runId = Guid.NewGuid();

        // Act
        var s1 = sut.TryStartProcessing(runId, "step1");
        var s2 = sut.TryStartProcessing(runId, "step2");
        var s3 = sut.TryStartProcessing(runId, "step3");

        // Assert
        Assert.True(s1);
        Assert.True(s2);
        Assert.True(s3);
    }

    [Fact]
    public void EndProcessing_UnknownKey_IsIdempotentNoop()
    {
        // Arrange — defensive: a finally-block calling EndProcessing for a step that was
        // never registered (e.g., deserialise failed before TryStartProcessing) must not throw.
        var sut = CreateSut();

        // Act & Assert — no exception
        sut.EndProcessing(Guid.NewGuid(), "never-registered");
    }
}
