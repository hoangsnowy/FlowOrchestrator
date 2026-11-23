using FlowOrchestrator.Dashboard;
using FlowOrchestrator.Dashboard.Webhooks;

namespace FlowOrchestrator.Dashboard.UnitTests;

/// <summary>
/// 2.0 enforces the webhook gates a trigger declares by default. Under the 1.x default (<c>Off</c>) the
/// pipeline accepted every request before any gate ran, so a declared HMAC key verified nothing.
/// </summary>
public sealed class WebhookEnforcementDefaultTests
{
    [Fact]
    public void WebhookSecurityOptions_DefaultsToEnforce()
    {
        // Arrange
        var options = new WebhookSecurityOptions();

        // Act
        var mode = options.EnforcementMode;

        // Assert
        Assert.Equal(WebhookEnforcementMode.Enforce, mode);
    }

    [Fact]
    public void FlowDashboardOptions_DefaultsToEnforce()
    {
        // Arrange
        var options = new FlowDashboardOptions();

        // Act
        var mode = options.WebhookSecurity.EnforcementMode;

        // Assert
        Assert.Equal(WebhookEnforcementMode.Enforce, mode);
    }
}
