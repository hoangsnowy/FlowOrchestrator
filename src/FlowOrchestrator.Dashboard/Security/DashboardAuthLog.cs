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

    [LoggerMessage(EventId = 4103, Level = LogLevel.Error, Message = "Flow dashboard has no authentication configured in the '{Environment}' environment. It is read-only for anonymous callers: state-changing calls are refused and manifest secrets are masked. Configure FlowDashboard:BasicAuth or FlowDashboard:Authorization (OpenID Connect / Keycloak).")]
    public static partial void UnprotectedReadOnly(ILogger logger, string environment);

    [LoggerMessage(EventId = 4104, Level = LogLevel.Error, Message = "Flow dashboard has no authentication configured in the '{Environment}' environment and FlowDashboard:AllowAnonymousOperators is true: anyone who can reach it can trigger, re-run, cancel and signal flows, change schedules, and read webhook secrets. Configure FlowDashboard:BasicAuth or FlowDashboard:Authorization (OpenID Connect / Keycloak).")]
    public static partial void UnprotectedAnonymousOperators(ILogger logger, string environment);

    [LoggerMessage(EventId = 4105, Level = LogLevel.Warning, Message = "Flow dashboard has no authentication configured. Anonymous callers have full operator access because the host runs in the Development environment; outside Development the dashboard becomes read-only. Configure FlowDashboard:BasicAuth or FlowDashboard:Authorization before deploying.")]
    public static partial void UnprotectedInDevelopment(ILogger logger);

    [LoggerMessage(EventId = 4106, Level = LogLevel.Warning, Message = "{Count} WaitForSignal step(s) restrict who may deliver their signal (allowedRoles / authorizationPolicy), but FlowDashboard:Authorization is not configured. Only an identity signed in through it can satisfy a restriction, so these signals cannot be delivered over HTTP; deliver them in-process with IFlowSignalDispatcher or configure identity sign-in.")]
    public static partial void RestrictedSignalsWithoutIdentitySignIn(ILogger logger, int count);

    [LoggerMessage(EventId = 4107, Level = LogLevel.Information, Message = "Restricted signal '{SignalName}' delivered to step '{StepKey}' of run {RunId} by '{User}'.")]
    public static partial void RestrictedSignalDelivered(ILogger logger, string signalName, string? stepKey, Guid runId, string? user);

    [LoggerMessage(EventId = 4108, Level = LogLevel.Warning, Message = "Signal '{SignalName}' for run {RunId} refused for '{User}' ({Method}): {Reason}.")]
    public static partial void SignalDeliveryDenied(ILogger logger, string signalName, Guid runId, string? user, string method, string reason);

    [LoggerMessage(EventId = 4109, Level = LogLevel.Warning, Message = "FlowDashboard:Authorization:UnregisteredFlowSignals is OperatorRule: a signal for a run whose flow this host does not register is delivered under the operator rule, so allowedRoles / authorizationPolicy on its WaitForSignal step are not enforced over HTTP. Register the same flows on the dashboard host and use Deny (the default) where you can.")]
    public static partial void UnregisteredFlowSignalsUseOperatorRule(ILogger logger);
}
