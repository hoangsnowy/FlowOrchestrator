using System.Security.Claims;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Dashboard.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FlowOrchestrator.Dashboard.UnitTests.Security;

/// <summary>
/// Pins how per-signal restrictions are read from <c>WaitForSignal</c> manifest inputs, which steps a signal
/// name can reach, and how a caller is checked against them — the pieces the signal endpoint, run detail and
/// the startup validation share.
/// </summary>
public sealed class SignalAuthorizationTests
{
    private static StepMetadata Wait(params (string Key, object? Value)[] inputs)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var (key, value) in inputs)
            dictionary[key] = value;
        return new StepMetadata { Type = "WaitForSignal", Inputs = dictionary };
    }

    private static ClaimsPrincipal SignedIn(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "oidc", nameType: "preferred_username", roleType: "roles"));

    private static HttpContext Http(Action<Microsoft.AspNetCore.Authorization.AuthorizationOptions>? policies = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAuthorization(o => policies?.Invoke(o));
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    // ── Reading a restriction ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Unrestricted_and_non_signal_steps_have_no_requirement()
    {
        // Arrange
        var plain = Wait(("signalName", "approval"));
        var explicitNulls = Wait(("signalName", "approval"), ("allowedRoles", null), ("authorizationPolicy", null));
        var other = new StepMetadata { Type = "LogMessage", Inputs = new Dictionary<string, object?> { ["allowedRoles"] = new[] { "x" } } };

        // Act
        var results = new[] { SignalAuthorization.Read("a", plain), SignalAuthorization.Read("b", explicitNulls), SignalAuthorization.Read("c", other) };

        // Assert
        Assert.All(results, Assert.Null);
    }

    [Theory]
    [MemberData(nameof(RoleListShapes))]
    public void Allowed_roles_are_read_from_every_list_shape(object roles)
    {
        // Arrange
        var step = Wait(("signalName", "approval"), ("allowedRoles", roles));

        // Act
        var requirement = SignalAuthorization.Read("wait", step);

        // Assert
        Assert.NotNull(requirement);
        Assert.Null(requirement.Error);
        Assert.Equal(["finance-approver", "cfo"], requirement.AllowedRoles);
        Assert.Null(requirement.Policy);
    }

    /// <summary>Shapes a manifest value takes: code-built arrays and lists, and JSON-bound elements.</summary>
    public static TheoryData<object> RoleListShapes() => new()
    {
        new[] { "finance-approver", "cfo" },
        new List<string> { "finance-approver", "cfo" },
        new object[] { "finance-approver", "cfo" },
        JsonDocument.Parse("""["finance-approver","cfo"]""").RootElement.Clone(),
    };

    [Fact]
    public void Input_names_are_matched_case_insensitively_like_the_typed_input_binder()
    {
        // Arrange
        var step = Wait(("SignalName", "approval"), ("AllowedRoles", new[] { "finance-approver" }), ("AUTHORIZATIONPOLICY", "FinanceLead"));

        // Act
        var requirement = SignalAuthorization.Read("wait", step);

        // Assert
        Assert.NotNull(requirement);
        Assert.Equal(["finance-approver"], requirement.AllowedRoles);
        Assert.Equal("FinanceLead", requirement.Policy);
    }

    [Theory]
    [InlineData("single string")]
    [InlineData("empty")]
    [InlineData("expression role")]
    [InlineData("blank role")]
    [InlineData("expression policy")]
    [InlineData("expression signal name")]
    [InlineData("duplicate key")]
    [InlineData("non-string role")]
    public void Malformed_restrictions_carry_an_error(string shape)
    {
        // Arrange
        var step = shape switch
        {
            "single string" => Wait(("signalName", "a"), ("allowedRoles", "finance-approver")),
            "empty" => Wait(("signalName", "a"), ("allowedRoles", Array.Empty<string>())),
            "expression role" => Wait(("signalName", "a"), ("allowedRoles", new[] { "@triggerBody()?.role" })),
            "blank role" => Wait(("signalName", "a"), ("allowedRoles", new[] { " " })),
            "expression policy" => Wait(("signalName", "a"), ("authorizationPolicy", "@triggerBody()?.policy")),
            "expression signal name" => Wait(("signalName", "@triggerBody()?.signal"), ("allowedRoles", new[] { "r" })),
            "duplicate key" => Wait(("signalName", "a"), ("allowedRoles", new[] { "r" }), ("ALLOWEDROLES", new[] { "s" })),
            _ => Wait(("signalName", "a"), ("allowedRoles", new object[] { 42 })),
        };

        // Act
        var requirement = SignalAuthorization.Read("wait", step);

        // Assert
        Assert.NotNull(requirement);
        Assert.NotNull(requirement.Error);
    }

    // ── Which steps a signal name reaches ───────────────────────────────────────────────────────

    [Fact]
    public void Signal_name_reaches_restricted_steps_at_any_depth_and_ignores_case()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["top"] = Wait(("signalName", "Approval"), ("allowedRoles", new[] { "a" })),
            ["other"] = Wait(("signalName", "unrelated"), ("allowedRoles", new[] { "b" })),
            ["open"] = Wait(("signalName", "approval")),
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                Steps = new StepCollection { ["inner"] = Wait(("signalName", "approval"), ("authorizationPolicy", "P")) },
            },
        };

        // Act
        var requirements = SignalAuthorization.ForSignal(steps, " approval ");

        // Assert
        Assert.Equal(["top", "loop.inner"], requirements.Select(r => r.StepKey));
    }

    [Fact]
    public void Restricted_step_with_a_dynamic_signal_name_reaches_every_name_and_fails_closed()
    {
        // Arrange
        var steps = new StepCollection { ["dyn"] = Wait(("signalName", "@triggerBody()?.signal"), ("allowedRoles", new[] { "a" })) };

        // Act
        var requirements = SignalAuthorization.ForSignal(steps, "anything");

        // Assert
        var requirement = Assert.Single(requirements);
        Assert.NotNull(requirement.Error);
    }

    [Fact]
    public void Default_signal_name_applies_when_the_step_declares_none()
    {
        // Arrange
        var steps = new StepCollection { ["wait"] = Wait(("allowedRoles", new[] { "a" })) };

        // Act
        var named = SignalAuthorization.ForSignal(steps, "default");
        var other = SignalAuthorization.ForSignal(steps, "approval");

        // Assert
        Assert.Single(named);
        Assert.Empty(other);
    }

    // ── Checking a caller ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Caller_without_a_signed_in_identity_never_satisfies_a_restriction()
    {
        // Arrange
        var requirement = new SignalRequirement("wait", ["a"], null);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        // Act
        var none = await SignalAuthorization.AuthorizeAsync(Http(), null, [requirement]);
        var unauthenticated = await SignalAuthorization.AuthorizeAsync(Http(), anonymous, [requirement]);

        // Assert
        Assert.Equal(SignalAuthorizationOutcome.NotSignedIn, none);
        Assert.Equal(SignalAuthorizationOutcome.NotSignedIn, unauthenticated);
    }

    [Fact]
    public async Task Any_allowed_role_satisfies_whichever_role_claim_type_carries_it()
    {
        // Arrange
        var requirement = new SignalRequirement("wait", ["finance-approver", "cfo"], null);

        // Act
        var viaRoles = await SignalAuthorization.AuthorizeAsync(Http(), SignedIn(new Claim("roles", "cfo")), [requirement]);
        var viaRoleClaimType = await SignalAuthorization.AuthorizeAsync(Http(), SignedIn(new Claim(ClaimTypes.Role, "finance-approver")), [requirement]);
        var wrongRole = await SignalAuthorization.AuthorizeAsync(Http(), SignedIn(new Claim("roles", "flow-operator")), [requirement]);
        var caseDiffers = await SignalAuthorization.AuthorizeAsync(Http(), SignedIn(new Claim("roles", "CFO")), [requirement]);

        // Assert
        Assert.Equal(SignalAuthorizationOutcome.Allowed, viaRoles);
        Assert.Equal(SignalAuthorizationOutcome.Allowed, viaRoleClaimType);
        Assert.Equal(SignalAuthorizationOutcome.MissingRole, wrongRole);
        Assert.Equal(SignalAuthorizationOutcome.MissingRole, caseDiffers);
    }

    [Fact]
    public async Task Policy_is_evaluated_and_an_unregistered_one_fails_closed()
    {
        // Arrange
        var http = Http(o => o.AddPolicy("FinanceLead", p => p.RequireClaim("finance_lead", "true")));
        var policy = new SignalRequirement("wait", [], "FinanceLead");
        var unknown = new SignalRequirement("wait", [], "Missing");

        // Act
        var passes = await SignalAuthorization.AuthorizeAsync(http, SignedIn(new Claim("finance_lead", "true")), [policy]);
        var fails = await SignalAuthorization.AuthorizeAsync(http, SignedIn(new Claim("roles", "flow-operator")), [policy]);
        var misconfigured = await SignalAuthorization.AuthorizeAsync(http, SignedIn(new Claim("finance_lead", "true")), [unknown]);

        // Assert
        Assert.Equal(SignalAuthorizationOutcome.Allowed, passes);
        Assert.Equal(SignalAuthorizationOutcome.PolicyFailed, fails);
        Assert.Equal(SignalAuthorizationOutcome.Misconfigured, misconfigured);
    }

    [Fact]
    public async Task Every_requirement_must_hold_and_a_malformed_one_denies_everybody()
    {
        // Arrange
        var finance = new SignalRequirement("a", ["finance-approver"], null);
        var legal = new SignalRequirement("b", ["legal-approver"], null);
        var broken = new SignalRequirement("c", [], null, Error: "bad");
        var both = SignedIn(new Claim("roles", "finance-approver"), new Claim("roles", "legal-approver"));

        // Act
        var financeOnly = await SignalAuthorization.AuthorizeAsync(Http(), SignedIn(new Claim("roles", "finance-approver")), [finance, legal]);
        var all = await SignalAuthorization.AuthorizeAsync(Http(), both, [finance, legal]);
        var withBroken = await SignalAuthorization.AuthorizeAsync(Http(), both, [finance, broken]);

        // Assert
        Assert.Equal(SignalAuthorizationOutcome.MissingRole, financeOnly);
        Assert.Equal(SignalAuthorizationOutcome.Allowed, all);
        Assert.Equal(SignalAuthorizationOutcome.Misconfigured, withBroken);
    }

    // ── The shared decision ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unregistered_flow_is_refused_under_Deny_and_follows_the_operator_rule_when_opted_in()
    {
        // Arrange
        var operatorAccess = new DashboardAccess("basic", "admin", CanOperate: true);
        var viewerAccess = new DashboardAccess("oidc", "vera", CanOperate: false);

        // Act
        var denied = await SignalAuthorization.DecideAsync(Http(), operatorAccess, null, "approval", UnregisteredFlowSignalMode.Deny);
        var operatorRule = await SignalAuthorization.DecideAsync(Http(), operatorAccess, null, "approval", UnregisteredFlowSignalMode.OperatorRule);
        var viewerRule = await SignalAuthorization.DecideAsync(Http(), viewerAccess, null, "approval", UnregisteredFlowSignalMode.OperatorRule);

        // Assert
        Assert.False(denied.Allowed);
        Assert.True(denied.FlowUnregistered);
        Assert.Contains("not registered", denied.DenialReason);
        Assert.True(operatorRule.Allowed);
        Assert.False(viewerRule.Allowed);
        Assert.False(viewerRule.FlowUnregistered);
    }

    [Fact]
    public async Task Restricted_signal_ignores_the_operator_role_and_unrestricted_one_requires_it()
    {
        // Arrange
        var flow = Substitute.For<IFlowDefinition>();
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["wait_finance"] = Wait(("signalName", "finance"), ("allowedRoles", new[] { "finance-approver" })),
                ["wait_manager"] = Wait(("signalName", "manager")),
            },
        });
        var http = Http();
        http.User = SignedIn(new Claim("roles", "flow-operator"));
        var operatorAccess = new DashboardAccess("oidc", "otto", CanOperate: true);

        // Act
        var restricted = await SignalAuthorization.DecideAsync(http, operatorAccess, flow, "finance", UnregisteredFlowSignalMode.Deny);
        var unrestricted = await SignalAuthorization.DecideAsync(http, operatorAccess, flow, "manager", UnregisteredFlowSignalMode.Deny);

        // Assert
        Assert.False(restricted.Allowed);
        Assert.True(restricted.IsRestricted);
        Assert.True(unrestricted.Allowed);
        Assert.False(unrestricted.IsRestricted);
    }

    [Theory]
    [InlineData("""{"signalName":"resolved-name"}""", "resolved-name")]
    [InlineData("""{"SIGNALNAME":" spaced "}""", "spaced")]
    [InlineData("""{"other":1}""", "from-manifest")]
    [InlineData("not json", "from-manifest")]
    [InlineData(null, "from-manifest")]
    public void Signal_name_of_a_parked_step_prefers_the_resolved_input(string? inputJson, string expected)
    {
        // Arrange
        var template = Wait(("signalName", "from-manifest"));

        // Act
        var name = SignalAuthorization.SignalNameOf(inputJson, template);

        // Assert
        Assert.Equal(expected, name);
        Assert.Equal("default", SignalAuthorization.SignalNameOf(null, null));
    }

    // ── Wording and options ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Restriction_is_described_by_role_and_policy_names_only()
    {
        // Arrange
        var requirement = new SignalRequirement("wait", ["finance-approver"], "FinanceLead");

        // Act
        var text = SignalAuthorization.Describe([requirement]);

        // Assert
        Assert.Equal("Requires role: finance-approver and policy: FinanceLead, signed in through the identity provider.", text);
    }

    [Fact]
    public void Additional_schemes_are_trimmed_and_deduplicated_against_the_primary_scheme()
    {
        // Arrange
        var options = new FlowDashboardAuthorizationOptions
        {
            AuthenticationScheme = "Cookies",
            AdditionalAuthenticationSchemes = [" Bearer ", "", "Cookies", "Bearer", "Mtls"],
        };

        // Act
        var schemes = options.EffectiveAdditionalSchemes;

        // Assert
        Assert.Equal(["Bearer", "Mtls"], schemes);
        Assert.Empty(new FlowDashboardAuthorizationOptions().EffectiveAdditionalSchemes);
    }
}
