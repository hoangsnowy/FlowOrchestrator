using System.Reflection;
using FlowOrchestrator.Hangfire.Telemetry;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// Locks the 2.0 public surface of <c>FlowOrchestrator.Hangfire</c>: the job target is
/// <see langword="internal"/>, while the types Hangfire serialises into queued jobs stay public so jobs
/// enqueued by any 2.x worker keep resolving.
/// </summary>
public sealed class ImplementationTypeVisibilityTests
{
    /// <summary>The job target registered by <c>UseHangfire()</c> is not public.</summary>
    [Fact]
    public void HangfireFlowOrchestrator_is_not_public()
    {
        // Arrange
        var type = typeof(HangfireFlowOrchestrator);

        // Act
        var visible = type.IsVisible;

        // Assert
        Assert.False(visible);
    }

    /// <summary>Hangfire's copy of the activity helpers is private to the package, like Core's.</summary>
    [Fact]
    public void ActivityExtensions_is_not_public()
    {
        // Arrange
        var type = typeof(ActivityExtensions);

        // Act
        var visible = type.IsVisible;

        // Assert
        Assert.False(visible);
    }

    /// <summary>The serialised job contracts stay public.</summary>
    /// <param name="type">A type named in queued Hangfire job payloads.</param>
    [Theory]
    [InlineData(typeof(IHangfireStepRunner))]
    [InlineData(typeof(IHangfireFlowTrigger))]
    [InlineData(typeof(HangfireStepEnvelope))]
    public void Serialized_job_contract_stays_public(Type type)
    {
        // Arrange
        var name = type.FullName;

        // Act
        var visible = type.IsVisible;

        // Assert
        Assert.True(visible, $"{name} is part of queued job payloads and must stay public.");
    }

    /// <summary>
    /// The envelope's conversion helpers are package internals: only the dispatcher and the job runner
    /// build and unpack envelopes.
    /// </summary>
    [Fact]
    public void HangfireStepEnvelope_exposes_no_public_methods_beyond_its_data()
    {
        // Arrange
        var type = typeof(HangfireStepEnvelope);

        // Act
        var declared = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();

        // Assert
        Assert.Empty(declared);
    }
}
