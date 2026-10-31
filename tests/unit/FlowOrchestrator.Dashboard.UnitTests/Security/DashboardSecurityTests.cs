using System.Security.Claims;
using FlowOrchestrator.Dashboard.Security;

namespace FlowOrchestrator.Dashboard.UnitTests.Security;

/// <summary>
/// Pins the pieces of dashboard access control that do not need a host: Keycloak role mapping,
/// the weak Basic Auth password check, and which HTTP methods count as state-changing.
/// </summary>
public sealed class DashboardSecurityTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "oidc", nameType: "preferred_username", roleType: "roles"));

    [Fact]
    public async Task Keycloak_realm_and_client_roles_become_role_claims()
    {
        // Arrange
        var principal = Principal(
            new Claim("realm_access", "{\"roles\":[\"flow-viewer\",\"offline_access\"]}"),
            new Claim("resource_access", "{\"flow-dashboard\":{\"roles\":[\"flow-operator\"]},\"other\":{\"roles\":[\"admin\"]}}"));
        var sut = new KeycloakRoleClaimsTransformation("flow-dashboard");

        // Act
        var result = await sut.TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("flow-viewer"));
        Assert.True(result.IsInRole("flow-operator"));
        Assert.True(result.IsInRole("offline_access"));
        Assert.False(result.IsInRole("admin"));
    }

    [Fact]
    public async Task Keycloak_client_roles_of_every_client_without_a_client_id()
    {
        // Arrange
        var principal = Principal(new Claim("resource_access", "{\"a\":{\"roles\":[\"r1\"]},\"b\":{\"roles\":[\"r2\"]}}"));

        // Act
        var result = await new KeycloakRoleClaimsTransformation().TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("r1"));
        Assert.True(result.IsInRole("r2"));
    }

    [Fact]
    public async Task Keycloak_transformation_is_idempotent()
    {
        // Arrange — authentication runs the transformation on every request.
        var principal = Principal(new Claim("realm_access", "{\"roles\":[\"flow-operator\"]}"));
        var sut = new KeycloakRoleClaimsTransformation();

        // Act
        await sut.TransformAsync(principal);
        var result = await sut.TransformAsync(principal);

        // Assert
        Assert.Single(result.FindAll("roles"), c => c.Value == "flow-operator");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[\"flow-operator\"]")]
    [InlineData("{\"roles\":\"flow-operator\"}")]
    [InlineData("{\"roles\":[1,null,\"\"]}")]
    public async Task Keycloak_malformed_role_claims_add_nothing(string realmAccess)
    {
        // Arrange
        var principal = Principal(new Claim("realm_access", realmAccess));

        // Act
        var result = await new KeycloakRoleClaimsTransformation().TransformAsync(principal);

        // Assert
        Assert.Empty(result.FindAll("roles"));
    }

    [Fact]
    public async Task Keycloak_transformation_ignores_anonymous_principals()
    {
        // Arrange
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access", "{\"roles\":[\"flow-operator\"]}")]));

        // Act
        var result = await new KeycloakRoleClaimsTransformation().TransformAsync(principal);

        // Assert
        Assert.False(result.IsInRole("flow-operator"));
    }

    [Theory]
    [InlineData("admin", "admin", true)]
    [InlineData("ops", "password", true)]
    [InlineData("ops", "Changeme", true)]
    [InlineData("ops", "short-pass", true)]
    [InlineData("operations-user", "operations-user", true)]
    [InlineData("ops", "", true)]
    [InlineData("ops", "correct-horse-battery", false)]
    public void Weak_basic_auth_passwords_are_detected(string user, string password, bool weak)
    {
        // Arrange
        var basic = new FlowDashboardBasicAuthOptions { Username = user, Password = password };

        // Act
        var result = DashboardServiceCollectionExtensions.IsWeakBasicAuthPassword(basic);

        // Assert
        Assert.Equal(weak, result);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("OPTIONS", false)]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    public void State_changing_methods_need_operate(string method, bool mutating)
    {
        // Arrange

        // Act
        var result = DashboardAccessFilter.IsMutating(method);

        // Assert
        Assert.Equal(mutating, result);
    }

    [Fact]
    public void Role_defaults_apply_only_when_not_configured()
    {
        // Arrange
        var defaults = new FlowDashboardAuthorizationOptions();
        var custom = new FlowDashboardAuthorizationOptions { ViewerRoles = [], OperatorRoles = ["ops"], AuthenticationScheme = "Cookies" };

        // Act + Assert
        Assert.Equal(["flow-viewer"], defaults.EffectiveViewerRoles);
        Assert.Equal(["flow-operator"], defaults.EffectiveOperatorRoles);
        Assert.Empty(custom.EffectiveViewerRoles);
        Assert.Equal(["ops"], custom.EffectiveOperatorRoles);
        Assert.Equal("Cookies", custom.EffectiveChallengeScheme);
        Assert.False(defaults.IsEnabled);
    }
}
