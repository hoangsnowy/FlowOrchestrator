# Security Policy

The FlowOrchestrator maintainers take security seriously. This document explains how to report a vulnerability and what you can expect in return.

---

## Supported versions

We provide security fixes for the **latest minor release** of each supported line. The full policy is in
[Support and Release Policy](docs/articles/support-policy.md).

| Version    | Runtimes              | Status       | Security fixes |
| ---------- | --------------------- | ------------ | -------------- |
| `2.x`      | .NET 10, .NET 11      | Current      | ✅ Yes         |
| `1.34.x`   | .NET 8, .NET 9, .NET 10 | Maintenance, until six months after the 2.0.0 release | ✅ Security and critical fixes |
| `< 1.34`   | —                     | End of life  | ❌ No          |

If you depend on a version no longer supported, the recommended action is to upgrade to the latest minor of a supported line — patch and minor releases are non-breaking by SemVer.

---

## Reporting a vulnerability

**Please do not open a public GitHub issue for security problems.**

Use one of the following private channels:

1. **GitHub Private Vulnerability Reporting** *(preferred)*
   Open an advisory at <https://github.com/hoangsnowy/FlowOrchestrator/security/advisories/new>.
   GitHub notifies the maintainers privately and gives us a coordinated workspace to triage, draft a fix, and request a CVE.

2. **Email**
   If you cannot use the GitHub UI, email **nguyenminhhoang.dit12@gmail.com** with the subject line `[SECURITY] FlowOrchestrator — <short description>`.

Encrypted communication is available on request — reply to the acknowledgement email and we will exchange a public key.

### What to include

To help us triage quickly, please provide:

- A clear description of the issue and the impact (data exposure, RCE, auth bypass, DoS, etc.).
- The affected version(s) and the platform / runtime where you reproduced it.
- A minimal reproduction — code snippet, manifest, HTTP request, or failing test.
- Any proof-of-concept or exploit script (kept private; we will never publish without your consent).
- Whether you intend to disclose publicly, and your preferred timeline.

You do **not** need a finished PoC to report — early heads-up on a suspected issue is welcome.

---

## What to expect from us

| Stage                    | Target time          |
| ------------------------ | -------------------- |
| Initial acknowledgement  | within **3 business days** |
| Triage + severity rating | within **7 calendar days** |
| Fix or mitigation plan   | within **30 calendar days** for high/critical |
| Coordinated disclosure   | mutually agreed; default **90 days** from report |

We follow a **coordinated disclosure** model. Once a fix is ready we will:

1. Cut a patch release on every supported minor.
2. Publish a GitHub Security Advisory with credit to you (unless you prefer to remain anonymous).
3. Request a CVE via GitHub's CNA.
4. Note the fix in [`CHANGELOG.md`](CHANGELOG.md) under a `### Security` heading, referencing the CVE.

If we do not respond within the window above, please escalate by replying to your original report — the inbox is monitored personally.

---

## Scope

### In scope

- Anything in `src/` that ships in a published NuGet package (`FlowOrchestrator.Core`, `FlowOrchestrator.Hangfire`, `FlowOrchestrator.InMemory`, `FlowOrchestrator.SqlServer`, `FlowOrchestrator.PostgreSQL`, `FlowOrchestrator.ServiceBus`, `FlowOrchestrator.Dashboard`, `FlowOrchestrator.Testing`).
- The dashboard's HTTP surface (`/flows`, `/flows/api/*`, webhook endpoints) — auth bypass, IDOR, SSRF, injection, header smuggling, etc.
- Persistence layer SQL — injection, race-condition double-execution, claim-guard bypass, dispatch-ledger corruption.
- Expression resolver (`@triggerBody()`, `@triggerHeaders()`) — sandbox escape, code execution, information disclosure.
- Trigger paths — webhook secret validation, idempotency-key replay, cron parser.

### Out of scope

- The sample apps under `samples/` — they exist to demonstrate usage and intentionally use weak local credentials. Vulnerabilities there are acceptable unless they leak into a published library API.
- Integration test fixtures (`tests/integration/**`) — likewise intentionally permissive.
- Findings that require physical access to the host or a compromised process credential.
- Denial of service via resource exhaustion when the operator has not enabled the webhook rate limiter (`WebhookSecurityOptions.RateLimit`, or the per-trigger `webhookRateLimitPermitsPerSecond` / `webhookRateLimitBurstSize` / `webhookRateLimitPerIp` inputs) and has not set a run timeout (`FlowRunControlOptions.DefaultRunTimeout`) — this is a configuration concern.
- Vulnerabilities in transitive dependencies that already have a published advisory and a fix the operator can apply by version-pinning. Please report these to the upstream project; we will follow up with a release that bumps the floor version.

---

## Hardening checklist for operators

These are **deployment** concerns, not library bugs, but they materially affect your production posture:

- Put the dashboard behind your existing auth proxy (OIDC, mTLS, internal-only ingress). The built-in Basic Auth is a defence-in-depth control, not a primary perimeter.
- Set a strong `webhookHmacKey` on every webhook trigger's manifest `Inputs` and supply it from configuration or a secret store — do not commit it to source. Rotate without downtime by moving the outgoing key to `webhookHmacKeyPrevious` before replacing `webhookHmacKey`; both are accepted during the overlap. The v1.24 names `webhookSecret` / `webhookSecretPrevious` still work as a fallback but are legacy — setting both raises a startup warning and `webhookHmacKey` wins.
- Use the SQL Server / PostgreSQL persistence with **least-privilege** credentials — the engine needs `SELECT/INSERT/UPDATE/DELETE` on the `Flow*` tables plus `WebhookReplayNonces` and `WebhookRejections`, and `CREATE TABLE` / `CREATE INDEX` / `ALTER TABLE` while the migrator runs. The migrator is a hosted service that re-checks the schema on **every** startup, not just the first; if you would rather not grant standing DDL rights, apply the schema out-of-band and revoke them afterwards.
- Enable TLS on every database connection string and Service Bus namespace.
- Pin the Hangfire dashboard behind separate auth — `app.UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = ... })`.
- Subscribe to this repo's **Security advisories** via the *Watch → Custom → Security alerts* dropdown so you get notified the moment a CVE is published.

---

## Hall of fame

We credit every reporter who follows this process responsibly. To be added to the in-repo acknowledgements list, simply ask in your report or in the advisory thread.

Thank you for helping keep FlowOrchestrator and its users safe.
