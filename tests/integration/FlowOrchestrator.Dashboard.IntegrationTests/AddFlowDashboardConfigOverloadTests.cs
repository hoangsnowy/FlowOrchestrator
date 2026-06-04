using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks;
using Microsoft.Extensions.Configuration;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down the <c>AddFlowDashboard(IConfiguration, Action&lt;FlowDashboardOptions&gt;)</c> overload:
/// Basic Auth must bind from configuration (so it is actually enforced) while the delegate still runs
/// for code-only settings such as webhook enforcement. Guards the upgrade footgun where switching to
/// the delegate-only overload silently dropped config-driven Basic Auth.
/// </summary>
public sealed class AddFlowDashboardConfigOverloadTests
{
    private static readonly string BasicAuthHeader =
        Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret123"));

    private static IConfiguration BasicAuthConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FlowDashboard:BasicAuth:Username"] = "admin",
                ["FlowDashboard:BasicAuth:Password"] = "secret123",
            })
            .Build();

    [Fact]
    public async Task Binds_BasicAuth_from_config_so_request_without_auth_is_401()
    {
        // Arrange
        using var server = new DashboardTestServer(BasicAuthConfig(), _ => { });
        server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        using var client = server.CreateClient();

        // Act
        var response = await client.GetAsync("/flows/api/flows");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Allows_request_with_credentials_bound_from_config()
    {
        // Arrange
        using var server = new DashboardTestServer(BasicAuthConfig(), _ => { });
        server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", BasicAuthHeader);

        // Act
        var response = await client.GetAsync("/flows/api/flows");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Runs_the_delegate_after_binding_config()
    {
        // Arrange
        var delegateRan = false;
        using var server = new DashboardTestServer(BasicAuthConfig(), opts =>
        {
            delegateRan = true;
            opts.UseWebhookSecurity(sec => sec.UseEnforcementMode(WebhookEnforcementMode.Enforce));
        });
        server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", BasicAuthHeader);

        // Act
        var response = await client.GetAsync("/flows/api/flows");

        // Assert
        Assert.True(delegateRan);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
