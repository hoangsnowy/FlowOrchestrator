# Webhook Hardening (v1.25)

The dashboard webhook receive endpoint
(`POST /flows/api/webhook/{idOrSlug}`) ships an opt-in security pipeline
that turns it from a developer convenience into a production-grade public
ingestion surface. The pipeline runs four gates in order:

```
IpAllowlist → BodySizeCap → SignatureVerify → ReplayCheck → RateLimit → IdempotencyDedup → Dispatch
```

Each gate is opt-in via manifest fields and is a no-op when not configured.
With the default `WebhookEnforcementMode = Off` the endpoint behaves exactly
as in v1.24 — every gate is skipped.

## Three enforcement modes

| Mode | Gate behaviour | HTTP response | When to use |
|------|----------------|---------------|-------------|
| `Off` | Skipped | Same as v1.24 | Greenfield + flows that haven't migrated |
| `Audit` | Run + log + metrics + DLQ | Always 202 (accept) | One release before flipping to `Enforce` to confirm legitimate traffic still validates |
| `Enforce` | Run + log + metrics + DLQ + reject | 4xx on failure | Production lock-down |

Configure globally:

```csharp
builder.Services.AddFlowDashboard(opts => opts.UseWebhookSecurity(sec =>
{
    sec.UseEnforcementMode(WebhookEnforcementMode.Audit);
    sec.UseMaxBodyBytes(1_048_576);
    sec.UseReplayProtection(toleranceSeconds: 300);
    sec.UseRateLimit(permitsPerSecond: 50, burstSize: 100, perIp: true);
    sec.UseForwardedHeaders(depth: 1);
}));
```

## Per-publisher cookbook

Each example shows the manifest inputs needed for the named publisher. The
HMAC verifier is generic — set `webhookSignatureScheme` and the wire
format is resolved through `PartnerSchemeRegistry`. Use `Custom` for any
publisher not on the built-in list.

### GitHub (`X-Hub-Signature-256`)

```csharp
["webhook"] = new TriggerMetadata
{
    Type = TriggerType.Webhook,
    Inputs = new Dictionary<string, object?>
    {
        ["webhookSlug"] = "github-events",
        ["webhookHmacKey"] = "<secret configured in github.com/.../settings/hooks>",
        ["webhookSignatureScheme"] = "GitHub",
        ["webhookReplayToleranceSeconds"] = 300,
        ["webhookNonceHeader"] = "X-GitHub-Delivery",
        ["webhookIpAllowListPreset"] = "github",
    }
}
```

### Stripe (`Stripe-Signature`)

Multi-value `t=…,v1=…` header. The verifier parses the timestamp out of
`t=` and uses every `v1=` candidate (Stripe rotates the second signing
key during rotation windows).

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "stripe-events",
    ["webhookHmacKey"] = "<whsec_… from Stripe dashboard>",
    ["webhookSignatureScheme"] = "Stripe",
    ["webhookReplayToleranceSeconds"] = 300,   // Stripe's recommended window
    ["webhookIpAllowListPreset"] = "stripe",
}
```

### Slack (`X-Slack-Signature`)

`v0=` digest over `v0:{ts}:{body}`. Slack supplies the timestamp in
`X-Slack-Request-Timestamp`.

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "slack-commands",
    ["webhookHmacKey"] = "<signing secret from api.slack.com app>",
    ["webhookSignatureScheme"] = "Slack",
    ["webhookReplayToleranceSeconds"] = 300,
}
```

### Shopify (`X-Shopify-Hmac-SHA256`)

Base64 SHA-256 over the raw body.

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "shopify-orders",
    ["webhookHmacKey"] = "<webhook secret from Shopify admin>",
    ["webhookSignatureScheme"] = "Shopify",
}
```

### Twilio (`X-Twilio-Signature`)

SHA-1 base64 over `{absoluteUrl}{sortedFormParams}`. Twilio sends
`application/x-www-form-urlencoded`, not JSON — make sure your reverse
proxy preserves the form body verbatim. Twilio is the one built-in scheme
that requires `AllowLegacySha1 = true`.

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "twilio-sms",
    ["webhookHmacKey"] = "<Auth Token from Twilio console>",
    ["webhookSignatureScheme"] = "Twilio",
}
```

### Square, Zoom, Linear, Dropbox, Calendly, Bitbucket, Atlassian, Microsoft Teams

Same shape — set `webhookSignatureScheme` to the publisher name. See
`PartnerSchemeRegistry` for the exact spec each one resolves to.

### Mailgun (body-resident signature)

Mailgun puts the signature triple (`timestamp`, `token`, `signature`) inside
the JSON body, not headers. The verifier extracts them and HMACs
`{timestamp}{token}`.

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "mailgun-events",
    ["webhookHmacKey"] = "<Mailgun API key>",
    ["webhookSignatureScheme"] = "Mailgun",
}
```

### Custom (full manifest control)

When the publisher is not on the built-in list, drive the verifier directly
from the manifest. Every aspect of the wire format is configurable:

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "myapp-events",
    ["webhookHmacKey"] = "<secret>",
    ["webhookSignatureScheme"] = "Custom",
    ["webhookSignatureHeader"] = "X-MyApp-Signature",
    ["webhookSignatureAlgorithm"] = "sha512",
    ["webhookSignatureEncoding"] = "base64",
    ["webhookSignaturePrefix"] = "v1=",
    ["webhookSignedPayloadStrategy"] = "TimestampDotBody",
    ["webhookSignedPayloadDelimiter"] = ".",
    ["webhookRequireTimestamp"] = true,
    ["webhookTimestampHeader"] = "X-MyApp-Timestamp",
    ["webhookReplayToleranceSeconds"] = 600,
}
```

For an even more exotic byte composition (e.g. AWS SigV4-style
canonical request), register a custom strategy in DI and reference it
by name:

```csharp
opts.UseWebhookSecurity(sec =>
    sec.AddCustomSignatureStrategy("myapp-canonical", ctx => /* byte[] */));
```

Then set `webhookSignedPayloadStrategy = "Custom"` and
`webhookCustomStrategyName = "myapp-canonical"` on the manifest.

## Zero-downtime key rotation

Set both `webhookHmacKey` (current) and `webhookHmacKeyPrevious` (rotated-out)
on the trigger. The verifier hashes the request against both keys without
short-circuiting (timing-safe). Successful matches against the previous key
emit `EventId 4010 WebhookSecretRotationUsedPrevious` so you can monitor
when it's safe to retire the old value.

```csharp
Inputs["webhookHmacKey"] = "new-secret-2026-Q2";
Inputs["webhookHmacKeyPrevious"] = "old-secret-2026-Q1";
```

The legacy `webhookSecret` (bearer-token shared secret) is also honoured
for the rotation pair via `webhookSecretPrevious`.

## Replay protection

Two complementary defences:

- **Skew window.** `webhookReplayToleranceSeconds` rejects any request whose
  timestamp drifts more than the configured number of seconds from the
  server's clock. `0` (default) disables the check.
- **Nonce ledger.** Every accepted request registers a
  `(flowId, triggerKey, nonce)` row in `IWebhookReplayStore`; a duplicate
  is a replay and gets `409 Conflict`. The nonce is either the explicit
  `webhookNonceHeader` (e.g. `X-GitHub-Delivery`) or
  `SHA-256(timestamp || body)` when no header is supplied.

The `WebhookReplayJanitor` background service purges expired entries
every minute. For multi-replica deployments use the SQL Server or
PostgreSQL backend (see [Storage](storage.md#webhook-hardening-backends-v125))
— the in-memory store is single-process and would let a replay through
on a second replica.

## Rate limiting

Token-bucket limiter built on `System.Threading.RateLimiting`, keyed
per-flow or per `flowId|clientIp` when `webhookRateLimitPerIp = true`.

```csharp
Inputs["webhookRateLimitPermitsPerSecond"] = 10;
Inputs["webhookRateLimitBurstSize"] = 20;
Inputs["webhookRateLimitPerIp"] = true;
```

429 responses include a `Retry-After` header and emit
`EventId 4003 WebhookRateLimited`.

## IP allow / deny list

`CidrMatcher` accepts every common notation for an IP allow / deny list,
mix-and-match in the same array:

| Notation | Example | Notes |
|----------|---------|-------|
| CIDR | `10.0.0.0/8`, `2001:db8::/32` | IPv4 + IPv6 |
| Single address | `203.0.113.42` | Auto-promoted to `/32` (IPv4) / `/128` (IPv6) |
| Inclusive range | `10.0.0.10-10.0.0.42` | Bytewise compare; works for IPv6 too (`2001:db8::1-2001:db8::ff`) |
| Octet wildcard | `10.0.*.*` | Equivalent to the matching CIDR (`10.0.0.0/16`); `*.*.*.*` matches everything |

```csharp
// Mix-and-match in any combination.
Inputs["webhookIpAllowList"] = new[]
{
    "10.0.0.0/8",                     // CIDR
    "172.16.0.10-172.16.0.42",        // inclusive range
    "192.168.5.*",                    // wildcard /24
    "203.0.113.42",                   // single address
    "2001:db8::/32",                  // IPv6 CIDR
    "::1/128",                        // IPv6 loopback
};

Inputs["webhookIpDenyList"] = new[]
{
    "203.0.113.0/24",                 // ban a /24
    "198.51.100.0-198.51.100.99",     // ban a range
};
```

The list can also be a single comma-delimited string, which is more friendly
for `appsettings.json` configuration:

```json
{
  "webhookIpAllowList": "10.0.0.0/8, 172.16.0.0-172.16.255.255, 192.168.*.*"
}
```

Allow takes precedence when both are set: a request must match the allow
list AND not match the deny list to pass. If only the allow list is set the
request must match it; if only the deny list is set every non-matching IP
passes.

### Curated publisher presets (`KnownPublisherCidrs`)

For known publishers, name a preset instead of pasting CIDRs into every flow:

```csharp
Inputs["webhookIpAllowListPreset"] = "github";
```

Presets bundled with the library:

| Preset | What it covers |
|--------|----------------|
| `github` | GitHub published webhook ranges (api.github.com/meta — 4 IPv4 + 2 IPv6) |
| `stripe` | Stripe webhook source IPs |
| `shopify` | Shopify partner-published ranges |
| `twilio` | Twilio "Network Connectivity" ranges |
| `square` | Square developer-docs ranges |
| `atlassian` / `bitbucket` | Atlassian Cloud / Bitbucket Cloud (ip-ranges.atlassian.com) |
| `slack` | Slack outbound IP ranges |
| `mailgun` | Mailgun control-panel IPs |
| `zoom` | Zoom marketplace publisher ranges |
| `local` / `localhost` / `private` | RFC 1918 private + loopback (IPv4 + IPv6 + link-local) — for dev environments |

Use multiple presets in one flow with `webhookIpAllowListPresets` (plural):

```csharp
// Array form
Inputs["webhookIpAllowListPresets"] = new[] { "github", "local" };

// Or comma-delimited string (appsettings.json friendly)
Inputs["webhookIpAllowListPresets"] = "github,stripe,local";
```

The plural form merges with the singular `webhookIpAllowListPreset` and the
explicit `webhookIpAllowList` — all three sources combine into a single
matcher. Use a custom array for environment-specific deltas; use presets for
the well-known partner ranges.

> **Caveat.** Presets are point-in-time snapshots of each publisher's
> documentation. They drift. For production lock-down treat them as a
> defensive baseline, then verify against the publisher's current
> authoritative IP-range page before relying on them solo. Combine with an
> explicit list when you need newer / extra ranges.

### Reverse-proxy / `X-Forwarded-For`

`WebhookSecurityOptions.ForwardedHeaderDepth` controls how deep into the
XFF chain the dashboard trusts:

```csharp
opts.UseWebhookSecurity(sec => sec.UseForwardedHeaders(depth: 1));
```

- `0` (default) — use `HttpContext.Connection.RemoteIpAddress` directly. Only
  trusts the immediate socket peer.
- `1` — trust 1 reverse-proxy hop; read the second-from-last entry in
  `X-Forwarded-For` as the client.
- `N` — trust N hops. Never set this higher than the actual number of
  proxies you control: every extra hop is an attacker-controlled IP if the
  client itself can spoof the header.

## Body size cap

`WebhookSecurityOptions.MaxBodyBytes` (default 1 MiB) is enforced via
`WebhookRequestBuffer.ReadAsync` before JSON parsing. Oversized requests
return `413 Payload Too Large` and emit `EventId 4004`.

## DLQ + dashboard surface

Every accepted and rejected delivery is persisted to
`IWebhookRejectionStore` (in-memory ring buffer, 1 000 entries by default,
or Sql/Postgres for long retention). The dashboard exposes:

- `GET /flows/api/webhooks/recent?flowId=&reason=&rejectedOnly=&skip=&take=`
  — listing with filters.
- `GET /flows/api/webhooks/stats?hours=24` — counts by reason for the
  configured look-back window.
- A "Webhooks" tab in the SPA renders both as a recent-deliveries table
  with reason chips and a 24-hour reason histogram.

## Observability

Counters and histograms (see [Observability](observability.md#metrics)):

- `webhook_received_total{flow,result,scheme}`
- `webhook_rejected_total{flow,reason}`
- `webhook_body_bytes`
- `webhook_processing_ms{flow,result}`

EventIds 4000–4099 reserved; 4000–4010 in use today (full list in
[Observability — EventIds](observability.md#logger-scopes-and-eventids)).

The existing `flow.webhook.receive` activity gains tags
`flow.webhook.scheme`, `flow.webhook.client_ip`,
`flow.webhook.replay_skew_ms`, `flow.webhook.rate_limit.retry_after_ms`,
`flow.webhook.result`, and `flow.webhook.reject_reason`.

## Recommended rollout

1. Ship `EnforcementMode = Audit` with `webhookSignatureScheme` populated
   on every webhook trigger. Verify legitimate traffic in the dashboard
   "Webhooks" tab; expect no rejections in the histogram.
2. Add `webhookReplayToleranceSeconds = 300` and IP allowlist / preset.
   Watch for skew-related rejections — clock-drifted publishers will
   surface here.
3. Add rate limit values that match your normal traffic envelope plus a
   reasonable burst.
4. Flip to `EnforcementMode = Enforce` once the audit period has passed.
5. For multi-replica deployments, register Sql or Postgres backends
   (`AddSqlServerWebhookHardening` / `AddPostgreSqlWebhookHardening`)
   before flipping to `Enforce` — otherwise replay protection is
   per-replica only.
