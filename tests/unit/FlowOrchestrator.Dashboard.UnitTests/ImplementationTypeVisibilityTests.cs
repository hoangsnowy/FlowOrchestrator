using System.Reflection;
using FlowOrchestrator.Dashboard.Notifications;
using FlowOrchestrator.Dashboard.Security;
using FlowOrchestrator.Dashboard.Webhooks;
using FlowOrchestrator.Dashboard.Webhooks.Dlq;
using FlowOrchestrator.Dashboard.Webhooks.Network;
using FlowOrchestrator.Dashboard.Webhooks.RateLimit;
using FlowOrchestrator.Dashboard.Webhooks.Replay;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks the 2.0 public surface of <c>FlowOrchestrator.Dashboard</c>: the SSE plumbing and the webhook
/// security implementations are <see langword="internal"/> and registered by <c>AddFlowDashboard()</c>;
/// the extension points (<see cref="IWebhookRateLimiter"/>, the options and their builder) stay public.
/// </summary>
public sealed class ImplementationTypeVisibilityTests
{
    /// <summary>Implementation types registered by <c>AddFlowDashboard()</c>.</summary>
    public static TheoryData<Type> InternalTypes => new()
    {
        typeof(SseConnection),
        typeof(SseEndpointHandler),
        typeof(SseFlowEventBroadcaster),
        typeof(InMemoryWebhookRejectionStore),
        typeof(InMemoryWebhookReplayStore),
        typeof(TokenBucketWebhookRateLimiter),
        typeof(ReplayProtectionGate),
        typeof(WebhookReplayJanitor),
        typeof(WebhookReplayRegistration),
        typeof(HmacSignatureVerifier),
        typeof(WebhookCustomVerifierRegistry),
        typeof(WebhookSignatureSpecResolver),
        typeof(WebhookRequestBuffer),
        typeof(WebhookSecurityPipeline),
        typeof(KeycloakRoleClaimsTransformation),
        typeof(CidrMatcher),
    };

    /// <summary>No implementation type is visible outside the dashboard assembly.</summary>
    /// <param name="type">The implementation type.</param>
    [Theory]
    [MemberData(nameof(InternalTypes))]
    public void Implementation_type_is_not_public(Type type)
    {
        // Arrange
        var name = type.FullName;

        // Act
        var visible = type.IsVisible;

        // Assert
        Assert.False(visible, $"{name} is public; it was made internal for 2.0.");
    }

    /// <summary>
    /// The builder is public — <c>UseWebhookSecurity(b => …)</c> hands it to the caller — but only the
    /// dashboard constructs it.
    /// </summary>
    [Fact]
    public void WebhookSecurityOptionsBuilder_is_public_with_no_public_constructor()
    {
        // Arrange
        var type = typeof(WebhookSecurityOptionsBuilder);

        // Act
        var publicConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        // Assert
        Assert.True(type.IsVisible);
        Assert.Empty(publicConstructors);
    }

    /// <summary>Custom webhook rate limiters still plug in through the public contract.</summary>
    [Fact]
    public void Rate_limiter_contract_stays_public()
    {
        // Arrange
        var type = typeof(IWebhookRateLimiter);

        // Act
        var visible = type.IsVisible;

        // Assert
        Assert.True(visible);
    }
}
