using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace FlowOrchestrator.Dashboard.Security;

/// <summary>
/// Turns Keycloak's nested role claims (<c>realm_access.roles</c> and
/// <c>resource_access.&lt;client&gt;.roles</c>) into ordinary role claims, so
/// <see cref="FlowDashboardAuthorizationOptions.ViewerRoles"/> / <see cref="FlowDashboardAuthorizationOptions.OperatorRoles"/>,
/// <c>IsInRole</c> and <c>RequireRole</c> work without custom code.
/// </summary>
/// <remarks>
/// Keycloak puts roles in JSON objects that ASP.NET Core does not map to <see cref="ClaimTypes.Role"/>. This
/// transformation reads both shapes and adds one claim per role using the identity's
/// <see cref="ClaimsIdentity.RoleClaimType"/>. It is idempotent: authentication runs it on every request,
/// so it marks the identity and skips one it already expanded. Register it with
/// <see cref="DashboardSecurityServiceCollectionExtensions.AddFlowDashboardKeycloakRoles"/>.
/// </remarks>
public sealed class KeycloakRoleClaimsTransformation : IClaimsTransformation
{
    private const string MarkerClaimType = "flow_dashboard_keycloak_roles";
    private readonly string? _clientId;

    /// <summary>Creates the transformation.</summary>
    /// <param name="clientId">
    /// Keycloak client whose <c>resource_access</c> roles are added; <see langword="null"/> adds client roles
    /// of every client in the token. Realm roles are always added.
    /// </param>
    public KeycloakRoleClaimsTransformation(string? clientId = null) => _clientId = clientId;

    /// <inheritdoc/>
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || identity.HasClaim(c => c.Type == MarkerClaimType))
        {
            return Task.FromResult(principal);
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in identity.FindAll("realm_access"))
            ReadRoles(claim.Value, roles);
        foreach (var claim in identity.FindAll("resource_access"))
            ReadClientRoles(claim.Value, _clientId, roles);

        foreach (var role in roles)
        {
            if (!identity.HasClaim(identity.RoleClaimType, role))
                identity.AddClaim(new Claim(identity.RoleClaimType, role));
        }
        identity.AddClaim(new Claim(MarkerClaimType, "1"));
        return Task.FromResult(principal);
    }

    private static void ReadRoles(string json, HashSet<string> roles)
    {
        if (!TryParse(json, out var doc))
            return;
        using (doc)
            AddRoles(doc.RootElement, roles);
    }

    private static void ReadClientRoles(string json, string? clientId, HashSet<string> roles)
    {
        if (!TryParse(json, out var doc))
            return;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;
            if (clientId is not null)
            {
                if (doc.RootElement.TryGetProperty(clientId, out var client))
                    AddRoles(client, roles);
                return;
            }
            foreach (var client in doc.RootElement.EnumerateObject())
                AddRoles(client.Value, roles);
        }
    }

    private static void AddRoles(JsonElement container, HashSet<string> roles)
    {
        if (container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty("roles", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } role)
                roles.Add(role);
        }
    }

    private static bool TryParse(string json, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
