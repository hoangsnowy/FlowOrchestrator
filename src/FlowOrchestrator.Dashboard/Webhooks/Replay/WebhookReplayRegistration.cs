namespace FlowOrchestrator.Dashboard.Webhooks.Replay;

/// <summary>
/// Handle to one replay-protection entry recorded by <see cref="ReplayProtectionGate"/> for a single request:
/// the exact key plus the secret owner token the entry was stored with.
/// </summary>
/// <param name="FlowId">Flow the entry is scoped to.</param>
/// <param name="TriggerKey">Trigger the entry is scoped to.</param>
/// <param name="Nonce">The nonce as registered (the replay key).</param>
/// <param name="RegistrationId">
/// Owner token generated for this request only (<see cref="Guid.NewGuid"/>). Holding it is what entitles the
/// request to release the entry; it is never derived from request data and never leaves the process.
/// </param>
internal readonly record struct WebhookReplayRegistration(Guid FlowId, string TriggerKey, string Nonce, Guid RegistrationId);
