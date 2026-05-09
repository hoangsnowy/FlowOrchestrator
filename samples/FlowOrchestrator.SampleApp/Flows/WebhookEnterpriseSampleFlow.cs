using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// WebhookEnterpriseSampleFlow — demonstrates the v1.25 webhook hardening pipeline
/// configured for GitHub's <c>X-Hub-Signature-256</c> dialect with replay protection,
/// rate limiting, and an IP allow list pulling from <c>KnownPublisherCidrs.GitHub</c>.
/// </summary>
/// <remarks>
/// To exercise locally:
/// <code>
/// curl -X POST http://localhost:5101/flows/api/webhook/github-sample-flow \
///   -H "X-Hub-Signature-256: sha256=$(printf '%s' '{"hello":"world"}' | openssl dgst -sha256 -hmac 'mySharedSecret' | cut -d' ' -f2)" \
///   -H "X-GitHub-Delivery: $(uuidgen)" \
///   -H "X-Webhook-Timestamp: $(date +%s)" \
///   -H 'Content-Type: application/json' \
///   --data '{"hello":"world"}'
/// </code>
/// Set <c>FlowDashboardOptions.UseWebhookSecurity(...)</c> to enable enforcement; with the
/// default <c>EnforcementMode.Off</c> the gates are skipped and the sample stays compatible
/// with hosts that have not opted in.
/// </remarks>
public sealed class WebhookEnterpriseSampleFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new Guid("d1f8a000-0000-0000-0000-000000000125");

    /// <summary>Schema version; bump on breaking changes to <see cref="Manifest"/>.</summary>
    public string Version => "1.0";

    /// <summary>Trigger + step manifest with full webhook-hardening configuration.</summary>
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?>
                {
                    ["webhookSlug"] = "github-sample-flow",
                    // Shared secret backs both signature verification AND legacy bearer auth.
                    ["webhookSecret"] = "mySharedSecret",
                    ["webhookHmacKey"] = "mySharedSecret",
                    // Active partner scheme — see PartnerSchemeRegistry.
                    ["webhookSignatureScheme"] = "GitHub",
                    // Replay protection: 5 min skew window, X-GitHub-Delivery as nonce.
                    ["webhookReplayToleranceSeconds"] = 300,
                    ["webhookTimestampHeader"] = "X-Webhook-Timestamp",
                    ["webhookNonceHeader"] = "X-GitHub-Delivery",
                    // Rate limit: 10 RPS per flow with burst of 20.
                    ["webhookRateLimitPermitsPerSecond"] = 10,
                    ["webhookRateLimitBurstSize"] = 20,
                    // Restrict source IPs. The sample includes the GitHub
                    // published webhook ranges PLUS loopback so the dev AppHost
                    // can fire test requests from `localhost`. In production
                    // drop the loopback entries and use the curated preset:
                    //   ["webhookIpAllowListPreset"] = "github"
                    ["webhookIpAllowList"] = new[]
                    {
                        "127.0.0.0/8", "::1/128",
                        "192.30.252.0/22", "185.199.108.0/22", "140.82.112.0/20",
                        "143.55.64.0/20", "2a0a:a440::/29", "2606:50c0::/32",
                    },
                },
            },
        },
        Steps = new StepCollection
        {
            ["log_payload"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "GitHub webhook received: @triggerBody()",
                },
            },
        },
    };
}
