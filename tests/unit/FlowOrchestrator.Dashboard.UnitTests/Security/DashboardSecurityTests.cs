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

        // Act
        var defaultViewers = defaults.EffectiveViewerRoles;
        var defaultOperators = defaults.EffectiveOperatorRoles;
        var customViewers = custom.EffectiveViewerRoles;
        var customOperators = custom.EffectiveOperatorRoles;
        var challenge = custom.EffectiveChallengeScheme;

        // Assert
        Assert.Equal(["flow-viewer"], defaultViewers);
        Assert.Equal(["flow-operator"], defaultOperators);
        Assert.Empty(customViewers);
        Assert.Equal(["ops"], customOperators);
        Assert.Equal("Cookies", challenge);
        Assert.False(defaults.IsEnabled);
    }

    [Theory]
    [InlineData("same-origin", null, false)]
    [InlineData("none", null, false)]
    [InlineData("same-site", null, true)]
    [InlineData("cross-site", null, true)]
    [InlineData(null, "https://flows.example.com", false)]
    [InlineData(null, "https://evil.example.com", true)]
    [InlineData(null, "null", true)]
    [InlineData(null, null, false)]
    [InlineData("same-origin", "https://evil.example.com", false)]
    public void Cross_site_detection_prefers_fetch_metadata_then_origin(string? fetchSite, string? origin, bool crossSite)
    {
        // Arrange — Sec-Fetch-Site wins because the browser computes it (proxy-proof); a request with
        // neither header is a script and is allowed.
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Host = new Microsoft.AspNetCore.Http.HostString("flows.example.com");
        if (fetchSite is not null) http.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        if (origin is not null) http.Request.Headers.Origin = origin;

        // Act
        var result = DashboardAccessFilter.IsCrossSiteRequest(http);

        // Assert
        Assert.Equal(crossSite, result);
    }

    [Fact]
    public void Viewer_manifest_masks_secret_values_anywhere_and_keeps_the_rest()
    {
        // Arrange
        const string manifest = """
            {"triggers":{"hook":{"type":"Webhook","inputs":{"webhookSecret":"s3cr3t","webhookHmacKey":"k","slug":"orders"}}},
             "steps":{"call":{"type":"Http","inputs":{"url":"https://x","ApiKey":"abc","headers":[{"authToken":"t"}],"retries":3,"nested":{"password":42}}}}}
            """;

        // Act
        var redacted = ManifestSecretRedactor.RedactJson(manifest)!;

        // Assert
        Assert.DoesNotContain("s3cr3t", redacted);
        Assert.DoesNotContain("\"abc\"", redacted);
        Assert.DoesNotContain("\"t\"", redacted);
        Assert.DoesNotContain("42", redacted);
        Assert.Contains("\"slug\":\"orders\"", redacted);
        Assert.Contains("\"retries\":3", redacted);
        Assert.Contains("https://x", redacted);
        Assert.Equal(5, redacted.Split(ManifestSecretRedactor.Mask).Length - 1);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("{not json", null)]
    public void Viewer_manifest_handles_missing_and_invalid_json(string? input, string? expected)
    {
        // Arrange

        // Act
        var redacted = ManifestSecretRedactor.RedactJson(input);

        // Assert
        Assert.Equal(expected, redacted);
    }

    [Theory]
    [InlineData("http://flows.corp", null, null, false)]
    [InlineData("http://flows.corp", "flows.corp", null, false)]
    [InlineData("http://flows.corp", null, "http://flows.corp", false)]
    [InlineData("http://flows.corp", null, "http://flows.corp/", false)]
    [InlineData("http://evil.corp", "flows.corp", "http://flows.corp", true)]
    public void Origin_check_accepts_forwarded_host_and_trusted_origins_behind_a_host_rewriting_proxy(string origin, string? forwardedHost, string? trusted, bool crossSite)
    {
        // Arrange — plain HTTP sends no Sec-Fetch-Site and the proxy rewrote Host to the upstream address.
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Host = new Microsoft.AspNetCore.Http.HostString(forwardedHost is null && trusted is null ? "flows.corp" : "backend:5000");
        http.Request.Headers.Origin = origin;
        if (forwardedHost is not null) http.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        string[]? trustedOrigins = trusted is null ? null : [trusted];

        // Act
        var result = DashboardAccessFilter.IsCrossSiteRequest(http, trustedOrigins);

        // Assert
        Assert.Equal(crossSite, result);
    }

    [Fact]
    public void Secret_names_are_masked_whatever_the_value_type()
    {
        // Arrange
        const string json = """{"apiKeys":["k1","k2"],"webhookSecret":{"value":"x"},"headers":{"Authorization":"Bearer abc"},"plain":{"keep":"me"}}""";

        // Act
        var redacted = ManifestSecretRedactor.RedactJson(json)!;

        // Assert
        Assert.DoesNotContain("k1", redacted);
        Assert.DoesNotContain("\"x\"", redacted);
        Assert.DoesNotContain("Bearer abc", redacted);
        Assert.Contains("\"keep\":\"me\"", redacted);
    }

    [Fact]
    public void Duplicate_property_names_yield_nothing_rather_than_throwing()
    {
        // Arrange
        const string json = """{"a":1,"a":2}""";

        // Act
        var redacted = ManifestSecretRedactor.RedactJson(json);

        // Assert
        Assert.Null(redacted);
    }

    [Fact]
    public void Run_copy_masks_trigger_body_step_and_attempt_payloads_without_touching_the_original()
    {
        // Arrange
        var run = new FlowOrchestrator.Core.Storage.FlowRunRecord
        {
            Id = Guid.NewGuid(),
            Status = "Failed",
            TriggerDataJson = """{"orderId":7,"token":"t-1"}""",
            Steps =
            [
                new FlowOrchestrator.Core.Storage.FlowStepRecord
                {
                    StepKey = "call",
                    StepType = "Http",
                    Status = "Failed",
                    InputJson = """{"url":"https://x","apiKey":"sk-live"}""",
                    OutputJson = """{"accessToken":"at"}""",
                    Attempts =
                    [
                        new FlowOrchestrator.Core.Storage.FlowStepAttemptRecord { StepKey = "call", StepType = "Http", Status = "Failed", InputJson = """{"apiKey":"sk-old"}""" },
                    ],
                },
            ],
        };

        // Act
        var copy = ManifestSecretRedactor.Redacted(run);

        // Assert
        var step = Assert.Single(copy.Steps!);
        Assert.DoesNotContain("t-1", copy.TriggerDataJson);
        Assert.Contains("\"orderId\":7", copy.TriggerDataJson);
        Assert.DoesNotContain("sk-live", step.InputJson);
        Assert.Contains("https://x", step.InputJson);
        Assert.DoesNotContain("\"at\"", step.OutputJson);
        Assert.DoesNotContain("sk-old", Assert.Single(step.Attempts!).InputJson);
        Assert.Contains("sk-live", run.Steps![0].InputJson);
    }
}
