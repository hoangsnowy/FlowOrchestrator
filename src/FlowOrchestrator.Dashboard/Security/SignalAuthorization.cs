using System.Collections;
using System.Security.Claims;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard.Security;

/// <summary>
/// What a restricted <c>WaitForSignal</c> step demands of whoever delivers its signal over HTTP.
/// </summary>
/// <param name="StepKey">Template key of the step that declared the restriction (as authored, no loop index).</param>
/// <param name="AllowedRoles">Roles of which the caller needs any one; empty when only a policy is required.</param>
/// <param name="Policy">Authorization policy the caller must satisfy; <see langword="null"/> when only roles are required.</param>
/// <param name="Error">
/// Why the step's restriction could not be read (malformed <c>allowedRoles</c>, an expression, …);
/// <see langword="null"/> when it is well formed. A requirement with an error is never satisfied — a
/// restriction the dashboard cannot read fails closed.
/// </param>
internal sealed record SignalRequirement(string StepKey, IReadOnlyList<string> AllowedRoles, string? Policy, string? Error = null);

/// <summary>Result of checking a caller against the requirements of one signal.</summary>
internal enum SignalAuthorizationOutcome
{
    /// <summary>The caller satisfies every requirement.</summary>
    Allowed,

    /// <summary>The caller is not an identity signed in through <c>FlowDashboard:Authorization</c> (Basic Auth or anonymous).</summary>
    NotSignedIn,

    /// <summary>The caller has none of the allowed roles.</summary>
    MissingRole,

    /// <summary>The caller does not satisfy the authorization policy.</summary>
    PolicyFailed,

    /// <summary>The policy is not registered, or the restriction is malformed: nobody can satisfy it.</summary>
    Misconfigured,
}

/// <summary>
/// Reads the <c>allowedRoles</c> / <c>authorizationPolicy</c> inputs of <c>WaitForSignal</c> steps from a flow
/// manifest and checks a caller against them. Used by the dashboard signal endpoint, by run detail (to tell
/// the page who may approve) and by the startup validation in <c>MapFlowDashboard</c>.
/// </summary>
/// <remarks>
/// <para>Resolution is by manifest, not by waiter row: every <c>WaitForSignal</c> step (at any nesting depth,
/// including inside <c>ForEach</c> scopes) whose static <c>signalName</c> matches the addressed name is a
/// candidate, and the caller must satisfy the restriction of every restricted candidate. The signal store
/// delivers to the oldest undelivered waiter with that name at write time, so authorizing against one waiter
/// looked up beforehand could be outrun by a concurrent delivery and land on a different step; the candidate
/// set is a superset of every step the write can reach, so no interleaving can bypass a restriction.</para>
/// <para>A restricted step whose <c>signalName</c> is not static matches every name (fail closed); the startup
/// validation rejects that configuration so it is only reachable when flows bypass it.</para>
/// <para>Cost: one walk over the manifest tree per signal request, O(steps); off every hot path.</para>
/// </remarks>
internal static class SignalAuthorization
{
    /// <summary>Manifest input listing the roles that may deliver the signal.</summary>
    internal const string AllowedRolesInput = "allowedRoles";

    /// <summary>Manifest input naming the authorization policy that may deliver the signal.</summary>
    internal const string PolicyInput = "authorizationPolicy";

    private const string SignalNameInput = "signalName";
    private const string DefaultSignalName = "default";

    /// <summary>
    /// Returns the restrictions of every <c>WaitForSignal</c> step that a delivery of
    /// <paramref name="signalName"/> could reach; empty when the signal is unrestricted.
    /// </summary>
    /// <param name="steps">The flow manifest's top-level steps.</param>
    /// <param name="signalName">The signal name from the request route.</param>
    /// <returns>The restricted candidates; empty when none is restricted.</returns>
    public static IReadOnlyList<SignalRequirement> ForSignal(StepCollection steps, string signalName)
    {
        List<SignalRequirement>? found = null;
        Collect(steps, signalName.Trim(), prefix: null, ref found);
        return found is null ? [] : found;
    }

    /// <summary>
    /// Reads the restriction declared on <paramref name="step"/>, or returns <see langword="null"/> when the
    /// step is not a <c>WaitForSignal</c> step or declares no restriction.
    /// </summary>
    /// <param name="stepKey">Template key of the step, recorded on the requirement.</param>
    /// <param name="step">The step metadata.</param>
    /// <returns>The requirement (possibly carrying an <see cref="SignalRequirement.Error"/>), or <see langword="null"/>.</returns>
    public static SignalRequirement? Read(string stepKey, StepMetadata step)
    {
        if (!string.Equals(step.Type, WaitForSignalHandler.StepTypeName, StringComparison.OrdinalIgnoreCase))
            return null;

        var hasRoles = TryGetInput(step.Inputs, AllowedRolesInput, out var rolesValue, out var rolesDuplicated);
        var hasPolicy = TryGetInput(step.Inputs, PolicyInput, out var policyValue, out var policyDuplicated);
        if (!hasRoles && !hasPolicy)
            return null;

        if (rolesDuplicated || policyDuplicated)
            return Invalid(stepKey, $"'{(rolesDuplicated ? AllowedRolesInput : PolicyInput)}' is declared more than once (input names are case-insensitive).");

        IReadOnlyList<string> roles = [];
        if (hasRoles && !IsNull(rolesValue))
        {
            if (!TryReadStringList(rolesValue, out var list))
                return Invalid(stepKey, $"'{AllowedRolesInput}' must be an array of role names.");
            if (list.Count == 0)
                return Invalid(stepKey, $"'{AllowedRolesInput}' is empty; list at least one role, or remove it to leave the signal unrestricted.");
            foreach (var role in list)
            {
                if (string.IsNullOrWhiteSpace(role))
                    return Invalid(stepKey, $"'{AllowedRolesInput}' contains a blank role name.");
                if (IsExpression(role))
                    return Invalid(stepKey, $"'{AllowedRolesInput}' must be static; '{role}' is an expression.");
            }
            roles = list;
        }

        string? policy = null;
        if (hasPolicy && !IsNull(policyValue))
        {
            if (!TryReadString(policyValue, out policy) || string.IsNullOrWhiteSpace(policy))
                return Invalid(stepKey, $"'{PolicyInput}' must be a policy name.");
            if (IsExpression(policy))
                return Invalid(stepKey, $"'{PolicyInput}' must be static; '{policy}' is an expression.");
            policy = policy.Trim();
        }

        if (roles.Count == 0 && policy is null)
            return null;   // both explicitly null: unrestricted

        if (!TryGetStaticSignalName(step, out _))
            return Invalid(stepKey, $"a restricted signal needs a static '{SignalNameInput}' so the dashboard can tell which step a delivery addresses before it writes anything.");

        return new SignalRequirement(stepKey, roles, policy);
    }

    /// <summary>
    /// Checks <paramref name="principal"/> against every requirement in <paramref name="requirements"/>.
    /// </summary>
    /// <param name="http">The request; supplies the authorization services.</param>
    /// <param name="principal">
    /// The identity signed in through <c>FlowDashboard:Authorization</c>, or <see langword="null"/> for a Basic
    /// Auth or anonymous caller, which never satisfies a restriction.
    /// </param>
    /// <param name="requirements">Requirements from <see cref="ForSignal"/> or <see cref="Read"/>.</param>
    /// <returns>The first failing outcome, or <see cref="SignalAuthorizationOutcome.Allowed"/>.</returns>
    /// <remarks>
    /// Roles are matched exactly as the viewer / operator roles are (<see cref="DashboardRoles.HasAnyRole"/>,
    /// so Keycloak roles mapped by <c>AddFlowDashboardKeycloakRoles</c> count). An unregistered policy is
    /// reported as <see cref="SignalAuthorizationOutcome.Misconfigured"/> rather than thrown, so a manifest that
    /// slipped past startup validation yields a 403 instead of a 500.
    /// </remarks>
    public static async ValueTask<SignalAuthorizationOutcome> AuthorizeAsync(
        HttpContext http,
        ClaimsPrincipal? principal,
        IReadOnlyList<SignalRequirement> requirements)
    {
        foreach (var requirement in requirements)
        {
            if (requirement.Error is not null)
                return SignalAuthorizationOutcome.Misconfigured;
        }

        if (principal?.Identity is not { IsAuthenticated: true })
            return SignalAuthorizationOutcome.NotSignedIn;

        foreach (var requirement in requirements)
        {
            if (requirement.AllowedRoles.Count > 0 && !DashboardRoles.HasAnyRole(principal, requirement.AllowedRoles))
                return SignalAuthorizationOutcome.MissingRole;

            if (requirement.Policy is { } policyName)
            {
                var policies = http.RequestServices.GetService<IAuthorizationPolicyProvider>();
                var authorization = http.RequestServices.GetService<IAuthorizationService>();
                var policy = policies is null ? null : await policies.GetPolicyAsync(policyName).ConfigureAwait(false);
                if (policy is null || authorization is null)
                    return SignalAuthorizationOutcome.Misconfigured;
                if (!(await authorization.AuthorizeAsync(principal, resource: null, policy).ConfigureAwait(false)).Succeeded)
                    return SignalAuthorizationOutcome.PolicyFailed;
            }
        }

        return SignalAuthorizationOutcome.Allowed;
    }

    /// <summary>
    /// Validates every restricted <c>WaitForSignal</c> step of every registered flow, at startup.
    /// </summary>
    /// <param name="flows">The registered flows.</param>
    /// <param name="services">The root service provider, used to look up authorization policies.</param>
    /// <returns>The number of restricted steps found.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a restriction is malformed, or names a policy that is not registered — the same fail-fast
    /// rule as <see cref="FlowDashboardAuthorizationOptions.ViewPolicy"/>.
    /// </exception>
    public static int ValidateFlows(FlowRegistry flows, IServiceProvider services)
    {
        var count = 0;
        HashSet<string>? checkedPolicies = null;
        foreach (var flow in flows.Flows)
        {
            List<SignalRequirement>? found = null;
            CollectAll(flow.Manifest.Steps, prefix: null, ref found);
            if (found is null)
                continue;

            foreach (var requirement in found)
            {
                count++;
                if (requirement.Error is not null)
                    throw new InvalidOperationException(
                        $"Flow '{flow.GetType().Name}' ({flow.Id}), step '{requirement.StepKey}': {requirement.Error}");

                if (requirement.Policy is not { } policy || !(checkedPolicies ??= new(StringComparer.Ordinal)).Add(policy))
                    continue;

                // Resolved in a scope: a host may register a scoped IAuthorizationPolicyProvider.
                using var scope = services.CreateScope();
                var provider = scope.ServiceProvider.GetService<IAuthorizationPolicyProvider>();
                if (provider?.GetPolicyAsync(policy).GetAwaiter().GetResult() is null)
                    throw new InvalidOperationException(
                        $"Flow '{flow.GetType().Name}' ({flow.Id}), step '{requirement.StepKey}' names the authorization policy '{policy}', which is not registered. Register it with AddAuthorization(o => o.AddPolicy(\"{policy}\", …)).");
            }
        }

        return count;
    }

    private static void Collect(StepCollection steps, string signalName, string? prefix, ref List<SignalRequirement>? found)
    {
        foreach (var (key, step) in steps)
        {
            var stepKey = prefix is null ? key : prefix + "." + key;
            if (Read(stepKey, step) is { } requirement
                && (!TryGetStaticSignalName(step, out var name) || string.Equals(name, signalName, StringComparison.OrdinalIgnoreCase)))
            {
                // Case-insensitive on purpose: the in-memory and SQL Server stores match signal names that way,
                // and a wider candidate set can only make the check stricter.
                (found ??= []).Add(requirement);
            }

            if (step is IScopedStep { Steps.Count: > 0 } scoped)
                Collect(scoped.Steps, signalName, stepKey, ref found);
        }
    }

    private static void CollectAll(StepCollection steps, string? prefix, ref List<SignalRequirement>? found)
    {
        foreach (var (key, step) in steps)
        {
            var stepKey = prefix is null ? key : prefix + "." + key;
            if (Read(stepKey, step) is { } requirement)
                (found ??= []).Add(requirement);
            if (step is IScopedStep { Steps.Count: > 0 } scoped)
                CollectAll(scoped.Steps, stepKey, ref found);
        }
    }

    private static SignalRequirement Invalid(string stepKey, string error) => new(stepKey, [], null, error);

    /// <summary>Reads a static, trimmed <c>signalName</c>, defaulting to <c>"default"</c> like the handler.</summary>
    private static bool TryGetStaticSignalName(StepMetadata step, out string name)
    {
        name = DefaultSignalName;
        if (!TryGetInput(step.Inputs, SignalNameInput, out var value, out var duplicated))
            return true;
        if (duplicated || !TryReadString(value, out var text))
            return false;
        if (text is null)
            return true;
        if (IsExpression(text))
            return false;
        name = text.Trim();
        return true;
    }

    /// <summary>Finds an input by name, ignoring case like the typed-input binder does.</summary>
    private static bool TryGetInput(IDictionary<string, object?> inputs, string name, out object? value, out bool duplicated)
    {
        value = null;
        duplicated = false;
        var found = false;
        foreach (var (key, candidate) in inputs)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                continue;
            duplicated = found;
            found = true;
            value = candidate;
        }
        return found;
    }

    private static bool IsNull(object? value)
        => value is null || value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    private static bool TryReadString(object? value, out string? text)
    {
        switch (value)
        {
            case null:
                text = null;
                return true;
            case string s:
                text = s;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                text = element.GetString();
                return true;
            case JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }:
                text = null;
                return true;
            default:
                text = null;
                return false;
        }
    }

    private static bool TryReadStringList(object? value, out List<string> items)
    {
        items = [];
        switch (value)
        {
            case string:
                return false;   // a single string is not a list; "a,b" would otherwise become one role named "a,b"
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (var element in array.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                        return false;
                    items.Add(element.GetString()!);
                }
                return true;
            case IEnumerable sequence:
                foreach (var item in sequence)
                {
                    if (!TryReadString(item, out var text) || text is null)
                        return false;
                    items.Add(text);
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>Same test the input resolver uses: the first non-blank character is <c>@</c>.</summary>
    private static bool IsExpression(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
                return c == '@';
        }
        return false;
    }
}

/// <summary>Role matching shared by the viewer / operator gate and per-signal restrictions.</summary>
internal static class DashboardRoles
{
    private static readonly string[] RoleClaimTypes = [ClaimTypes.Role, "role", "roles"];

    /// <summary>
    /// <see langword="true"/> when <paramref name="principal"/> has any of <paramref name="roles"/>, read through
    /// <see cref="ClaimsPrincipal.IsInRole"/> and from <c>role</c> / <c>roles</c> claims.
    /// </summary>
    /// <param name="principal">The signed-in user.</param>
    /// <param name="roles">Roles to look for; ordinal comparison.</param>
    /// <returns><see langword="true"/> on the first match.</returns>
    /// <remarks>
    /// OIDC handlers keep a JSON <c>roles</c> array as claims of type <c>roles</c>, which <c>IsInRole</c> does
    /// not read unless the identity's role claim type says so; Keycloak's nested roles become ordinary role
    /// claims through <see cref="KeycloakRoleClaimsTransformation"/>.
    /// </remarks>
    public static bool HasAnyRole(ClaimsPrincipal principal, IReadOnlyList<string> roles)
    {
        foreach (var role in roles)
        {
            if (principal.IsInRole(role))
                return true;
        }

        foreach (var claim in principal.Claims)
        {
            if (Array.IndexOf(RoleClaimTypes, claim.Type) >= 0 && Contains(roles, claim.Value))
                return true;
        }

        return false;
    }

    private static bool Contains(IReadOnlyList<string> roles, string value)
    {
        foreach (var role in roles)
        {
            if (string.Equals(role, value, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
