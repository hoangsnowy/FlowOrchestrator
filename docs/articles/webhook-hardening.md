# Webhook Hardening (v1.25)

The dashboard webhook receive endpoint
(`POST /flows/api/webhook/{idOrSlug}`) ships an opt-in security pipeline
that turns it from a developer convenience into a production-grade public
ingestion surface. The endpoint caps the request body before anything else,
then the pipeline runs four gates in order:

```
BodySizeCap (endpoint) → IpAllowlist → RateLimit → SignatureVerify → ReplayCheck → Dispatch
```

Idempotency dedupe is not a pipeline gate — it runs later, inside
`FlowOrchestratorEngine.TriggerAsync`, after the pipeline has accepted the
delivery.

Each gate is opt-in via manifest fields and is a no-op when not configured.
The default `WebhookEnforcementMode` is `Enforce` (since 2.0): every configured gate rejects violating
requests. With `Off` (the 1.x default) the endpoint behaves exactly as in v1.24 — every gate is skipped.

## Three enforcement modes

| Mode | Gate behaviour | HTTP response | When to use |
|------|----------------|---------------|-------------|
| `Off` | Skipped | Same as v1.24 | Explicit opt-out (the 1.x default) while a publisher is being fixed |
| `Audit` | Run + log + metrics + DLQ | Always 200 (accept) — except an oversized body, which is capped before the pipeline and still returns 413 | One release before flipping to `Enforce` to confirm legitimate traffic still validates |
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

## Just pick a preset

> [!IMPORTANT]
> **Since 2.0, `WebhookSecurityOptions.EnforcementMode` defaults to `Enforce`.** Every gate a
> trigger declares on this page — signature verification, replay protection, rate limiting, IP
> allow/deny — rejects violating requests. In 1.x the default was `Off`, under which the pipeline
> accepted the request before any gate ran and recorded nothing: a flow could declare an HMAC key and
> still accept unsigned requests.
>
> Change the mode once in DI:
>
> ```csharp
> builder.Services.AddFlowDashboard(builder.Configuration, opts => opts.UseWebhookSecurity(sec =>
>     sec.UseEnforcementMode(WebhookEnforcementMode.Audit)));
> ```
>
> `Audit` is the safe rollout step: it logs and counts violations while still accepting, so you can
> confirm a publisher's signatures verify before anything starts being rejected. The sample app runs
> in `Audit` deliberately — which is why a duplicate nonce there returns `200` and only moves a
> counter.

For the 16 built-in schemes in `PartnerSchemeRegistry` (14 publishers plus the
`Generic` catch-all and `GitHubLegacy`), the manifest side needs only
three fields. Pick a scheme and the verifier fills in the wire
format (header name, algorithm, encoding, prefix) automatically:

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "github-events",
    ["webhookHmacKey"] = "<secret from publisher dashboard>",
    ["webhookSignatureScheme"] = "GitHub",
}
```

Browse the [Per-publisher cookbook](#per-publisher-cookbook) for the
other 15 schemes. If your publisher isn't on the list, see
[Custom scheme reference](#custom-scheme-reference) for full manifest
control or [Bring your own scheme](#bring-your-own-scheme) to plug a
custom verifier through DI.

## Migrating from v1.24

v1.24 used `webhookSecret` / `webhookSecretPrevious` as the HMAC key
fields. v1.25 renames them to make the role explicit; the v1.24 names
keep working as aliases.

| v1.24 manifest field    | v1.25 equivalent          | Notes                                                                                                                                          |
| ----------------------- | ------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- |
| `webhookSecret`         | `webhookHmacKey`          | Legacy alias; honoured as fallback. `webhookHmacKey` wins when both are set; the pipeline emits EventId 4011 once per flow on the first conflict. |
| `webhookSecretPrevious` | `webhookHmacKeyPrevious`  | Same precedence rule as above.                                                                                                                 |

Existing v1.24 manifests using only `webhookSecret` continue to work
unchanged. When such a trigger also sets `webhookSignatureScheme`, a delivery
whose signature verifies with the `webhookSecret` key is accepted without an
`X-Webhook-Key` header; without a scheme, `X-Webhook-Key` is still required. Removal of the legacy names is planned for v2.0; no
`[Obsolete]` attribute is applied yet.

## Per-publisher cookbook

Each example shows the manifest inputs needed for the named publisher. The
HMAC verifier is generic — set `webhookSignatureScheme` and the wire
format is resolved through `PartnerSchemeRegistry`. Use `Custom` for any
publisher not on the built-in list.

| `webhookSignatureScheme` | Signature header | Signed content | Key bytes |
| --- | --- | --- | --- |
| `GitHub` | `X-Hub-Signature-256: sha256=<hex>` | raw body | UTF-8 |
| `GitHubLegacy` | `X-Hub-Signature: sha1=<hex>` (needs `AllowLegacySha1`) | raw body | UTF-8 |
| `Bitbucket`, `Atlassian` | `X-Hub-Signature: sha256=<hex>` | raw body | UTF-8 |
| `Stripe` | `Stripe-Signature: t=<ts>,v1=<hex>,…` | `{t}.{body}` | UTF-8 |
| `Calendly` | `Calendly-Webhook-Signature: t=<ts>,v1=<hex>` | `{t}.{body}` | UTF-8 |
| `Slack` | `X-Slack-Signature: v0=<hex>` + `X-Slack-Request-Timestamp` | `v0:{ts}:{body}` | UTF-8 |
| `Zoom` | `X-Zm-Signature: v0=<hex>` + `X-Zm-Request-Timestamp` | `v0:{ts}:{body}` | UTF-8 |
| `Shopify` | `X-Shopify-Hmac-SHA256: <base64>` | raw body | UTF-8 |
| `Twilio` | `X-Twilio-Signature: <base64>` (HMAC-SHA1, needs `AllowLegacySha1`) | `{url}{sorted form fields}` | UTF-8 |
| `Square` | `X-Square-HmacSha256-Signature: <base64>` | `{url}{body}` | UTF-8 |
| `Linear` | `Linear-Signature: <hex>` | raw body | UTF-8 |
| `Dropbox` | `X-Dropbox-Signature: <hex>` | raw body | UTF-8 |
| `Mailgun` | in the JSON body (`signature.signature`) | `{timestamp}{token}` | UTF-8 |
| `MicrosoftTeams` | `Authorization: HMAC <base64>` | raw body | base64-decoded token |
| `StandardWebhooks` | `webhook-signature: v1,<base64> …` + `webhook-id` + `webhook-timestamp` | `{webhook-id}.{webhook-timestamp}.{body}` | base64-decoded part after `whsec_` |
| `Generic` | `X-Signature: sha256=<hex>` | raw body | UTF-8 |
| `Custom` | from the [manifest fields](#custom-scheme-reference) | | |

All of them use HMAC-SHA256 unless noted, and compare every candidate signature
against the current and previous key in constant time.

A `webhookSignatureScheme` value that nothing can verify — not a built-in
name, not registered with `RegisterScheme` or `AddWebhookSignatureVerifier`,
or `Custom` with a signed-payload strategy that was never registered with
`AddCustomSignatureStrategy` — is a configuration error, not "unsigned":
`MapFlowDashboard` throws for a registered flow that names one (in `Enforce`
and `Audit`), and a delivery to such a trigger is rejected with `401` /
`signature_scheme_unknown` (`Audit` records it and lets it through; EventId
4012). Leave the input out, or set it to `None`, for an unsigned trigger.

### GitHub (`X-Hub-Signature-256`)

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "github-events",
    ["webhookHmacKey"] = "<secret from github.com/.../settings/hooks>",
    ["webhookSignatureScheme"] = "GitHub",
    ["webhookNonceHeader"] = "X-GitHub-Delivery",
    ["webhookIpAllowListPreset"] = "github",
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
proxy preserves the form body verbatim. Twilio and `GitHubLegacy` are the two
built-in schemes that require `AllowLegacySha1 = true` (both sign with HMAC-SHA1).

The webhook endpoint accepts form-encoded bodies for every webhook trigger. The
decoded fields are handed to the verifier, and the run's trigger body is a JSON
object with one string per field (a field that repeats becomes an array of its
values), so `@triggerBody()?.Body` and `@triggerBody()?.From` work as they would
for a JSON delivery. A form over the ASP.NET Core form limits (1,024 fields)
is answered `400 Invalid form payload.`; the `MaxBodyBytes` cap applies as for JSON.
Twilio signs a repeated parameter value by value — the verifier sees a repeated
field joined with `,`, so a Twilio request that repeats a parameter does not verify.
The absolute URL the verifier signs is the one the request arrived on; behind a
proxy that rewrites scheme or host, configure forwarded headers so it matches the
URL configured in Twilio.

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "twilio-sms",
    ["webhookHmacKey"] = "<Auth Token from Twilio console>",
    ["webhookSignatureScheme"] = "Twilio",
}
```

### Square, Zoom, Linear, Dropbox, Calendly, Bitbucket, Atlassian, MicrosoftTeams

Same shape — set `webhookSignatureScheme` to the exact enum name
(`"MicrosoftTeams"`, no space). Names are matched case-insensitively but must
otherwise be exact: an unrecognised name is rejected as described above
(`signature_scheme_unknown`). See `PartnerSchemeRegistry` for the exact spec
each one resolves to.

### Microsoft Teams (`Authorization: HMAC …`)

Teams signs the raw body with HMAC-SHA256 and sends the base64 digest in the
`Authorization` header. Paste the **security token Teams shows when you create
the outgoing webhook** into `webhookHmacKey` exactly as shown: it is base64, and
the verifier keys the HMAC with its decoded bytes, as
[Microsoft documents](https://learn.microsoft.com/microsoftteams/platform/webhooks-and-connectors/how-to/add-outgoing-webhook).
A key that is not valid base64 is rejected as `KeyNotConfigured`. The
`Authorization` header is handed to the verifier only — it is still never
captured as a trigger header or written to the delivery log.

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "teams-bot",
    ["webhookHmacKey"] = "<security token from the Teams outgoing-webhook dialog>",
    ["webhookSignatureScheme"] = "MicrosoftTeams",
}
```

### Standard Webhooks (Svix, Clerk, Resend, Lemon Squeezy, …)

[Standard Webhooks](https://www.standardwebhooks.com) is the open signing
scheme behind Svix and the providers that deliver through it. Per the
[specification](https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md),
each delivery carries `webhook-id`, `webhook-timestamp` (Unix seconds) and
`webhook-signature`, a space-delimited list of `v1,<base64 HMAC-SHA256>`
entries over `{webhook-id}.{webhook-timestamp}.{body}`. Paste the signing
secret into `webhookHmacKey` exactly as the provider shows it — `whsec_…`; the
verifier keys the HMAC with the base64-decoded part after the prefix (a secret
without the prefix works too). A secret that is not valid base64 is rejected as
`KeyNotConfigured`.

The delivery is accepted when **any** `v1` entry matches. Entries of other
versions (`v1a`, the asymmetric `v2a`) and malformed entries are ignored, so a
publisher rolling its secret can send one signature per secret — and a digest
under any label other than `v1` never verifies. A missing `webhook-signature` or
`webhook-id` is `MissingHeader`; a missing `webhook-timestamp` is
`MissingTimestamp`.

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "clerk-events",
    ["webhookHmacKey"] = "<whsec_… signing secret from the provider dashboard>",
    ["webhookSignatureScheme"] = "StandardWebhooks",
}
```

The spec asks receivers to check the timestamp against a tolerance (its
reference libraries use 5 minutes) and to use `webhook-id` as an idempotency key.
Both come from [replay protection](#replay-protection): turn it on with
`UseReplayProtection(toleranceSeconds: 300)` (or
`webhookReplayToleranceSeconds = 300` on the trigger). The skew window then
applies to the signed `webhook-timestamp`, and the signed `webhook-id` is the
replay key — once a message has been processed, a resend of the same message id
within the window is `409`, even though the publisher re-timestamps and re-signs
every retry. A first attempt that failed before its run started (a `5xx` from the
trigger, for example) does not count: its key is released, so the publisher's
retry is processed — see [Failed deliveries release their key](#failed-deliveries-release-their-key).

There is no `Svix` alias: use `StandardWebhooks` for every provider that signs
this way. (`Svix` stays an unknown scheme name and is rejected like any other.)

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

Use this only if your publisher isn't in the built-in list. ~10% of
users will need it. The full field set lives in
[Custom scheme reference](#custom-scheme-reference); a minimal example:

```csharp
Inputs = new Dictionary<string, object?>
{
    ["webhookSlug"] = "myapp-events",
    ["webhookHmacKey"] = "<secret>",
    ["webhookSignatureScheme"] = "Custom",
    ["webhookSignatureHeader"] = "X-MyApp-Signature",
    ["webhookSignatureAlgorithm"] = "sha512",
}
```

For exotic byte compositions (e.g. AWS SigV4-style canonical requests),
register a custom byte-builder strategy in DI and reference it by name —
see [Custom scheme reference](#custom-scheme-reference). For a fully
pluggable verifier (asymmetric, KMS, query-string carriers), see
[Bring your own scheme](#bring-your-own-scheme).

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

The v1.24 names `webhookSecret` / `webhookSecretPrevious` are legacy
aliases for the same rotation pair — see
[Migrating from v1.24](#migrating-from-v124).

## Replay protection
> Enforced only when `EnforcementMode` is `Enforce`. Under `Audit` violations are counted and logged but the request is still accepted; under `Off` nothing below takes effect at all. `Enforce` is the default since 2.0.

Two complementary defences:

- **Skew window.** `webhookReplayToleranceSeconds` rejects any request whose
  timestamp drifts more than the configured number of seconds from the
  server's clock. `0` (default) disables the check.
- **Nonce ledger.** Every accepted request registers a
  `(flowId, triggerKey, nonce)` row in `IWebhookReplayStore`; a duplicate
  is a replay and gets `409 Conflict`. The nonce is either the explicit
  `webhookNonceHeader` (e.g. `X-GitHub-Delivery`) or
  `SHA-256(timestamp || body)` when no header is supplied. A nonce longer
  than 256 characters is registered as `sha256:` + its hex digest
  (`WebhookReplayNonceStorage`), so it still dedupes on every backend.

Once a signature scheme has verified the request, only signed material counts,
because a replayer chooses every unsigned header:

- **Signed timestamp** (Stripe, Slack, Zoom, Calendly, Mailgun, StandardWebhooks):
  the skew window applies to the signed timestamp and the replay key is derived
  from it — or is the signed token (Mailgun `token`, Standard Webhooks
  `webhook-id`). Unsigned timestamp / nonce headers are ignored.
- **Body-only signature** (GitHub, Shopify, Linear, Dropbox, Bitbucket,
  Atlassian, Teams, …): no timestamp header is required — these publishers send
  none — and any unsigned one is ignored. The replay key is `SHA-256(body)`, held
  for `2 × webhookReplayToleranceSeconds` from the server clock: a byte-identical
  delivery inside that window is rejected `409`, and after it no replay protection
  is possible without a signed timestamp. Note that a publisher's own "redeliver"
  of the same payload inside the window is rejected too.
- **No signature**: a timestamp header is required (`409 missing_timestamp`
  otherwise) and the explicit `webhookNonceHeader`, when configured, is the key.

The `WebhookReplayJanitor` background service purges expired entries
every minute. For multi-replica deployments use the SQL Server or
PostgreSQL backend (see [Storage](storage.md#webhook-hardening-backends-v125))
— the in-memory store is single-process and would let a replay through
on a second replica.

### Failed deliveries release their key

The replay key is recorded when the request passes the gate, before the run is
started. If the request then fails without starting a run — the legacy
`X-Webhook-Key` check refuses it (`401`), the trigger throws (`5xx`), or the client
disconnects before the run is written — the endpoint releases the key again
(EventId 4014), so the publisher's retry is processed instead of being answered
`409`. Without this, a store outage during one delivery turned every retry inside
the window into a lost delivery, and a caller without the shared secret could
burn any `webhookNonceHeader` value it chose.

The release cannot open a replay window:

- Each registration carries a random owner token (`registrationId`) that never
  leaves the process; the store removes the entry only when the token matches. A
  replayer's own registration fails (the entry already exists), so it never holds
  the token of the entry that blocks it.
- It runs only on failure. A delivery whose trigger returned — run started,
  `Idempotency-Key` duplicate, or disabled flow (`202`) — keeps its key.
- It does not run once the engine has dispatched or skipped an entry step
  (`ITriggerContext.EntryStepsStarted`): that run is alive and recovery finishes
  it, so a retry would start a second one.
- It runs with `CancellationToken.None` and is best effort: if the store cannot
  be reached the original failure is still returned, EventId 4013 is logged, and
  the key expires as it always did.

Entries are owner-tagged in a nullable `RegistrationId` / `registration_id`
column that the SQL Server and PostgreSQL migrators add on startup; rows written
before the upgrade have no owner and simply expire.

## Rate limiting
> Enforced only when `EnforcementMode` is `Enforce`. Under `Audit` violations are counted and logged but the request is still accepted; under `Off` nothing below takes effect at all. `Enforce` is the default since 2.0.

Token-bucket limiter built on `System.Threading.RateLimiting`, keyed
per webhook trigger (`flowId|triggerKey`) or per `flowId|triggerKey|clientIp`
when `webhookRateLimitPerIp = true`. Two webhook triggers of one flow have
independent budgets, each sized by its own `webhookRateLimit*` inputs.

```csharp
Inputs["webhookRateLimitPermitsPerSecond"] = 10;
Inputs["webhookRateLimitBurstSize"] = 20;
Inputs["webhookRateLimitPerIp"] = true;
```

429 responses emit `EventId 4003 WebhookRateLimited`; the retry-after hint is
not sent as a `Retry-After` header — it is exposed on the
`flow.webhook.rate_limit.retry_after_ms` span tag.

## IP allow / deny list
> Enforced only when `EnforcementMode` is `Enforce`. Under `Audit` violations are counted and logged but the request is still accepted; under `Off` nothing below takes effect at all. `Enforce` is the default since 2.0.

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

`WebhookSecurityOptions.ForwardedHeaderDepth` is the **number of trusted
reverse proxies in front of the app** (load balancer, ingress, CDN …). It
decides which address the IP allow / deny lists and per-IP rate limits see:

```csharp
opts.UseWebhookSecurity(sec => sec.UseForwardedHeaders(depth: 1));
```

Every proxy **appends the address it received the request from** to
`X-Forwarded-For` (never its own). Behind N proxies the last N entries were
written by your proxies, and the leftmost of those — the entry N positions
from the right — is the caller. Anything further left came in the caller's
own header and is ignored. This is the same rule as ASP.NET Core's
`ForwardedHeadersMiddleware` with `ForwardLimit = N`.

- `0` (default) — ignore the header; use `HttpContext.Connection.RemoteIpAddress`.
- `1` — one proxy (e.g. nginx with `$proxy_add_x_forwarded_for`): the **last**
  entry is the caller. `X-Forwarded-For: 192.30.252.10, 203.0.113.50` resolves
  to `203.0.113.50`; the first entry was sent by the caller.
- `2` — e.g. CDN → ingress → app: the second entry from the right.
- If the header has fewer than N entries, the socket address is used.

Set it to exactly the number of proxies you operate. One too many lets a
caller choose its own address (allow-list bypass, rate-limit evasion); one too
few reports a proxy's address for every caller.

## Body size cap

`WebhookSecurityOptions.MaxBodyBytes` (default 1 MiB) is enforced via
`WebhookRequestBuffer.ReadAsync` before JSON parsing and before the security
pipeline runs, so the cap applies in every enforcement mode including `Audit`.
Oversized requests return `413 Payload Too Large`. (EventId 4004 is reserved for
this case but is not currently emitted.)

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

Counters and histograms (see [Observability](observability.md#what-is-emitted)):

- `webhook_received_total{flow,result,scheme}`
- `webhook_rejected_total{flow,reason}`
- `webhook_body_bytes`
- `webhook_processing_ms{flow,result}`

EventIds 4000–4099 reserved; 4000–4012 allocated today (4000, 4004 and 4009
are reserved but not currently emitted). Full list in
[Observability — EventIds](observability.md#logger-scopes-and-eventids).

The existing `flow.webhook.receive` activity gains tags
`flow.webhook.scheme`, `flow.webhook.client_ip`,
`flow.webhook.replay_skew_ms`, `flow.webhook.rate_limit.retry_after_ms`,
`flow.webhook.result`, and `flow.webhook.reject_reason`.

## Custom scheme reference

When `webhookSignatureScheme = "Custom"` the verifier composes its
behaviour from the manifest fields below instead of looking up a
built-in spec in `PartnerSchemeRegistry`. Most users will never need
these — pick a built-in scheme first.

### Signature fields

| Field                                  | Meaning                                                                                                |
| -------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| `webhookSignatureHeader`               | Header carrying the digest (e.g. `X-MyApp-Signature`).                                                 |
| `webhookSignatureAlgorithm`            | `sha1`, `sha256` (default), `sha384`, `sha512`. SHA-1 also requires `AllowLegacySha1`.                 |
| `webhookSignatureEncoding`             | `hexLower` (default), `hexUpper`, `base64`, `base64Url`.                                               |
| `webhookSignaturePrefix`               | Prefix stripped from the header value before decoding (e.g. `sha256=`).                                |
| `webhookSignatureMultiValueDelimiter`  | Multi-value separator (Stripe `,`, etc.); enables key=value parsing.                                   |
| `webhookSignatureKeyValueSeparator`    | Key/value separator inside each segment; defaults to `=`.                                              |
| `webhookSignatureValueKey`             | Segment key whose value is the signature (Stripe `v1`).                                                |
| `webhookHeaderValuePrefix`             | Prefix on the whole header line, stripped before parsing (Microsoft Teams `HMAC `).                    |
| `webhookAcceptMultipleSignatures`      | When `true`, every matching segment is treated as a candidate.                                         |
| `webhookAcceptedVersions`              | Allowlist of segment keys (`["v0", "v1"]`); other segments ignored.                                    |
| `webhookSignedPayloadStrategy`         | `RawBody` (default), `TimestampDotBody`, `ColonDelimited`, `UrlPlusSortedForm`, `UrlPlusBody`, `TimestampPlusToken`, `MessageIdTimestampBody`, `Custom`. |
| `webhookHmacKeyEncoding`               | `Utf8` (default) keys the HMAC with the key string's UTF-8 bytes; `Base64` with its decoded bytes (an optional `whsec_` prefix is stripped first). |
| `webhookMessageIdHeader`               | Header carrying the message id signed by `MessageIdTimestampBody` (Standard Webhooks `webhook-id`); it becomes the replay key. |
| `webhookIgnoreMalformedSignatureEntries` | When `true`, a multi-value entry without the key/value separator is skipped instead of failing the header as `MalformedHeader`. |
| `webhookSignedPayloadDelimiter`        | Delimiter between timestamp and body for delimited strategies.                                         |
| `webhookSignedPayloadVersion`          | Version literal injected for `ColonDelimited` (e.g. Slack `v0`).                                       |
| `webhookCustomStrategyName`            | Name of a strategy registered through `AddCustomSignatureStrategy(...)`; required when strategy = `Custom`. |

### Timestamp fields

| Field                       | Meaning                                                                                                       |
| --------------------------- | ------------------------------------------------------------------------------------------------------------- |
| `webhookTimestampHeader`    | Header that carries the publisher timestamp when it is not embedded in the signature header.                  |
| `webhookTimestampValueKey`  | Multi-value key whose value is the timestamp (Stripe `t`).                                                    |
| `webhookRequireTimestamp`   | When `true`, missing timestamps fail closed.                                                                  |

### Replay fields

| Field                              | Meaning                                                                                                       |
| ---------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| `webhookReplayToleranceSeconds`    | Maximum allowed clock skew. `0` (default) disables replay protection. Must be `0 … 2147483647`; a value outside that range fails `MapFlowDashboard` at startup. |
| `webhookNonceHeader`               | Header used for the nonce ledger (e.g. `X-GitHub-Delivery`); falls back to SHA-256 of `timestamp \|\| body`. |

### Rate-limit fields

| Field                                  | Meaning                                                                |
| -------------------------------------- | ---------------------------------------------------------------------- |
| `webhookRateLimitPermitsPerSecond`     | Steady-state token-bucket rate.                                        |
| `webhookRateLimitBurstSize`            | Maximum token burst.                                                   |
| `webhookRateLimitPerIp`                | When `true`, the bucket key is `flowId\|triggerKey\|clientIp` instead of `flowId\|triggerKey`. |

### IP allow / deny fields

| Field                              | Meaning                                                                                                       |
| ---------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| `webhookIpAllowList`               | Explicit list of CIDRs / ranges / wildcards / single addresses.                                               |
| `webhookIpDenyList`                | Same shape as the allow list; allow takes precedence when both are set.                                       |
| `webhookIpAllowListPreset`         | Single curated preset (`github`, `stripe`, …) drawn from `KnownPublisherCidrs`.                               |
| `webhookIpAllowListPresets`        | Plural form; comma-string or array. Merges with the singular form and the explicit list.                      |

For exotic byte compositions, register a custom strategy in DI and
reference it from `webhookCustomStrategyName`:

```csharp
opts.UseWebhookSecurity(sec =>
    sec.AddCustomSignatureStrategy("myapp-canonical", ctx => /* byte[] */));
```

```csharp
Inputs["webhookSignedPayloadStrategy"] = "Custom";
Inputs["webhookCustomStrategyName"] = "myapp-canonical";
```

## Register a named scheme without writing a verifier

When the wire format fits the built-in HMAC verifier but the publisher isn't in
`PartnerSchemeRegistry`, register a named spec once at startup instead of
repeating the `Custom` field set on every flow:

```csharp
builder.Services.AddFlowDashboard(opts => opts.UseWebhookSecurity(sec =>
    sec.RegisterScheme("AcmePartner", new WebhookSignatureSpec
    {
        HeaderName = "X-Acme-Signature",
        Algorithm = HmacAlgorithm.Sha256,
        Encoding = SignatureEncoding.HexLower,
        Prefix = "sha256=",
        SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
    })));
```

```csharp
Inputs["webhookSignatureScheme"] = "AcmePartner";
```

Registered names are matched case-insensitively and are checked *before* the
built-in enum, so registering a name that matches a built-in scheme shadows it.
The specs live on `WebhookSecurityOptions.CustomSchemes`.

## Bring your own scheme

When the wire format doesn't fit any built-in scheme nor the `Custom`
manifest shape — asymmetric verification, KMS-backed digests, query-string
signature carriers — register a full `IWebhookSignatureVerifier` in DI and
reference it by name from the manifest.

```csharp
public sealed class AcmeCorpVerifier : IWebhookSignatureVerifier
{
    public WebhookSignatureResult Verify(WebhookSignatureContext context)
    {
        // Read context.Headers / context.Body and verify against your KMS,
        // public key, etc. Return Success / SuccessWithRotation / Failure(reason).
        return WebhookSignatureResult.Success;
    }
}

builder.Services.AddWebhookSignatureVerifier<AcmeCorpVerifier>("AcmeCorp");
```

```csharp
Inputs["webhookSignatureScheme"] = "AcmeCorp";
```

The pipeline resolves in this order: DI-registered verifier
(`AddWebhookSignatureVerifier<T>`) → operator-registered spec (`RegisterScheme`)
→ built-in `WebhookSignatureScheme` enum match → `Custom` manifest shape. Scheme
names are case-insensitive; DI names can never shadow a built-in because
collisions with a built-in scheme name (or the literal `"Custom"`) throw
`ArgumentException` at registration time.

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
