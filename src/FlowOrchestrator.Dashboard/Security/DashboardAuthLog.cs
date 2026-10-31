using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Dashboard.Security;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> methods for dashboard access control. EventIds 4100–4199
/// are reserved for dashboard authentication events.
/// </summary>
internal static partial class DashboardAuthLog
{
    [LoggerMessage(EventId = 4100, Level = LogLevel.Warning, Message = "Flow dashboard Basic Auth is enabled in the '{Environment}' environment without identity-based sign-in. Basic Auth is one shared account with no roles; configure FlowDashboard:Authorization (OpenID Connect / Keycloak) for production.")]
    public static partial void BasicAuthOutsideDevelopment(ILogger logger, string environment);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning, Message = "Flow dashboard Basic Auth uses a weak password (a well-known default, shorter than 12 characters, or equal to the user name). Change FlowDashboard:BasicAuth:Password.")]
    public static partial void WeakBasicAuthPassword(ILogger logger);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Information, Message = "Flow dashboard sign-in: scheme '{AuthenticationScheme}', challenge '{ChallengeScheme}', Basic Auth {BasicAuth}.")]
    public static partial void IdentityAccessEnabled(ILogger logger, string authenticationScheme, string challengeScheme, string basicAuth);
}
