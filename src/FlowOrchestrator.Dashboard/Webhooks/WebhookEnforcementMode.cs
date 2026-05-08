namespace FlowOrchestrator.Dashboard.Webhooks;

/// <summary>
/// Operating mode for webhook security gates. Operators typically run a release
/// in <see cref="Audit"/> first to confirm that legitimate traffic still
/// validates, then flip to <see cref="Enforce"/> on the next release.
/// </summary>
public enum WebhookEnforcementMode
{
    /// <summary>Gates skipped entirely. Endpoint behaves exactly as it did before v1.25.0.</summary>
    Off = 0,

    /// <summary>Gates run, log + emit metrics + write to DLQ, but the endpoint always accepts (returns 202).</summary>
    Audit,

    /// <summary>Gates run and reject failing requests with the appropriate 4xx status.</summary>
    Enforce,
}
