using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// Shared shape for the provider webhook flows the e2e skill drives (<c>.claude/skills/e2e/e2e-webhooks.sh</c>):
/// one webhook trigger plus one step that logs the trigger body, so a delivery that gets through the
/// security gates produces a run whose trigger body and headers are visible in run detail.
/// </summary>
/// <remarks>
/// Secrets here are demo values committed on purpose so the e2e script can sign requests the way the
/// real provider would. Never reuse them in a real deployment.
/// </remarks>
public abstract class ProviderWebhookFlowBase : IFlowDefinition
{
    /// <summary>Creates the flow with a stable id and the trigger inputs for one provider.</summary>
    /// <param name="id">Stable flow id; must also be listed in the AppHost's <c>SampleFlowIds</c> (Service Bus subscriptions).</param>
    /// <param name="inputs">Webhook trigger inputs (slug, scheme, key, gate overrides).</param>
    protected ProviderWebhookFlowBase(Guid id, Dictionary<string, object?> inputs)
    {
        Id = id;
        Manifest = new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["webhook"] = new TriggerMetadata { Type = TriggerType.Webhook, Inputs = inputs },
            },
            Steps = new StepCollection
            {
                ["log_delivery"] = new StepMetadata
                {
                    Type = "LogMessage",
                    Inputs = new Dictionary<string, object?> { ["message"] = "Webhook delivery: @triggerBody()" },
                },
            },
        };
    }

    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; }

    /// <summary>Schema version; bump on breaking changes to <see cref="Manifest"/>.</summary>
    public string Version => "1.0";

    /// <summary>Single webhook trigger and a logging step.</summary>
    public FlowManifest Manifest { get; set; }
}

/// <summary>
/// GitHub delivery: <c>X-Hub-Signature-256</c> over the raw body only. GitHub sends no timestamp and no
/// <c>X-Webhook-Key</c>; the host-wide replay window still applies (identical redelivery → 409).
/// </summary>
public sealed class GitHubWebhookFlow() : ProviderWebhookFlowBase(new Guid("d1f8a000-0000-0000-0000-000000000201"), new()
{
    ["webhookSlug"] = "e2e-github",
    ["webhookSignatureScheme"] = "GitHub",
    ["webhookHmacKey"] = "e2e-github-secret",
});

/// <summary>Stripe delivery: <c>Stripe-Signature: t=…,v1=…</c> over <c>{t}.{body}</c> (signed timestamp).</summary>
public sealed class StripeWebhookFlow() : ProviderWebhookFlowBase(new Guid("d1f8a000-0000-0000-0000-000000000202"), new()
{
    ["webhookSlug"] = "e2e-stripe",
    ["webhookSignatureScheme"] = "Stripe",
    ["webhookHmacKey"] = "whsec" + "_e2eStripeSigningSecret000000000000",
});

/// <summary>Slack slash command: form-urlencoded body, <c>X-Slack-Signature: v0=…</c> over <c>v0:{ts}:{body}</c>.</summary>
public sealed class SlackWebhookFlow() : ProviderWebhookFlowBase(new Guid("d1f8a000-0000-0000-0000-000000000203"), new()
{
    ["webhookSlug"] = "e2e-slack",
    ["webhookSignatureScheme"] = "Slack",
    // The signing secret from Slack's own worked example (docs.slack.dev, "Verifying requests from Slack").
    ["webhookHmacKey"] = "8f742231b10e8888abcd99yyyzzz85a5",
});

/// <summary>
/// Twilio delivery: form-urlencoded POST, <c>X-Twilio-Signature</c> = base64 HMAC-SHA1 over
/// <c>{full URL}{sorted name+value pairs}</c>. Requires <c>AllowLegacySha1()</c> on the host.
/// </summary>
public sealed class TwilioWebhookFlow() : ProviderWebhookFlowBase(new Guid("d1f8a000-0000-0000-0000-000000000204"), new()
{
    ["webhookSlug"] = "e2e-twilio",
    ["webhookSignatureScheme"] = "Twilio",
    ["webhookHmacKey"] = "e2e-twilio-auth-token",
});

/// <summary>
/// Microsoft Teams outgoing webhook: <c>Authorization: HMAC &lt;base64&gt;</c>; the key is the base64 security
/// token exactly as Teams displays it (the verifier base64-decodes it).
/// </summary>
public sealed class TeamsWebhookFlow() : ProviderWebhookFlowBase(new Guid("d1f8a000-0000-0000-0000-000000000205"), new()
{
    ["webhookSlug"] = "e2e-teams",
    ["webhookSignatureScheme"] = "MicrosoftTeams",
    ["webhookHmacKey"] = "6fMbcEmK8Zdzj5A9xXk1yW3m7Hn2pQ4rT0uVwY8zB1c=",
});

/// <summary>
/// Standard Webhooks / Svix delivery: <c>webhook-id</c>, <c>webhook-timestamp</c>,
/// <c>webhook-signature: v1,&lt;base64&gt;</c> over <c>{id}.{ts}.{body}</c> with the base64-decoded part after <c>whsec_</c>.
/// </summary>
/// <remarks>
/// Verified by the built-in <c>StandardWebhooks</c> scheme; the secret is pasted exactly as the provider shows it
/// (<c>whsec_…</c>). Under the host's replay protection the signed <c>webhook-id</c> is the replay key, so the e2e
/// check sends a fresh message id per delivery.
/// </remarks>
public sealed class StandardWebhooksFlow() : ProviderWebhookFlowBase(new Guid("d1f8a000-0000-0000-0000-000000000206"), new()
{
    ["webhookSlug"] = "e2e-standard-webhooks",
    ["webhookSignatureScheme"] = "StandardWebhooks",
    // Secret from the Standard Webhooks library test suite.
    ["webhookHmacKey"] = "whsec_" + "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
});

/// <summary>
/// Plain shared secret: the caller sends <c>X-Webhook-Key</c> (or <c>Authorization: Bearer</c>), no HMAC.
/// Replay window disabled for this trigger so the idempotency check can resend the same body.
/// </summary>
public sealed class SharedSecretWebhookFlow() : ProviderWebhookFlowBase(new Guid("d1f8a000-0000-0000-0000-000000000207"), new()
{
    ["webhookSlug"] = "e2e-shared-secret",
    ["webhookSecret"] = "e2e-shared-secret",
    ["webhookReplayToleranceSeconds"] = 0,
});

/// <summary>Unsigned trigger with a tight per-trigger rate limit (1 permit/s, burst 2) for the 429 check.</summary>
public sealed class RateLimitedWebhookFlow() : ProviderWebhookFlowBase(new Guid("d1f8a000-0000-0000-0000-000000000208"), new()
{
    ["webhookSlug"] = "e2e-rate-limited",
    ["webhookReplayToleranceSeconds"] = 0,
    ["webhookRateLimitPermitsPerSecond"] = 1,
    ["webhookRateLimitBurstSize"] = 2,
});

/// <summary>
/// Unsigned trigger restricted to <c>198.51.100.0/24</c> (TEST-NET-2). Loopback is deliberately NOT listed, so
/// a direct call from the e2e host must be refused and a forged <c>X-Forwarded-For</c> prefix must not open it.
/// </summary>
public sealed class IpAllowListWebhookFlow() : ProviderWebhookFlowBase(new Guid("d1f8a000-0000-0000-0000-000000000209"), new()
{
    ["webhookSlug"] = "e2e-ip-allowlist",
    ["webhookReplayToleranceSeconds"] = 0,
    ["webhookIpAllowList"] = new[] { "198.51.100.0/24" },
});
