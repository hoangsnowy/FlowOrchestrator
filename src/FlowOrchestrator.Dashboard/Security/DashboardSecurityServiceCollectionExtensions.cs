using FlowOrchestrator.Dashboard.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication;

namespace FlowOrchestrator.Dashboard;

/// <summary>Registration helpers for signing in to the dashboard through an identity provider.</summary>
public static class DashboardSecurityServiceCollectionExtensions
{
    /// <summary>
    /// Maps Keycloak realm and client roles to ordinary role claims so the dashboard's viewer / operator
    /// role checks work against a Keycloak-issued identity.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="clientId">
    /// Keycloak client whose client roles are mapped (typically the dashboard's own client);
    /// <see langword="null"/> maps client roles of every client. Realm roles are always mapped.
    /// </param>
    /// <returns>The same <paramref name="services"/>.</returns>
    /// <remarks>
    /// ASP.NET Core resolves a single <see cref="IClaimsTransformation"/>, the last one registered. Call this
    /// after <c>AddAuthentication</c>; a host that already has its own transformation should call
    /// <see cref="KeycloakRoleClaimsTransformation.TransformAsync"/> from it instead.
    /// </remarks>
    public static IServiceCollection AddFlowDashboardKeycloakRoles(this IServiceCollection services, string? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IClaimsTransformation>(new KeycloakRoleClaimsTransformation(clientId));
        return services;
    }
}
