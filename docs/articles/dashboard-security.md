# Securing the dashboard

The dashboard (`/flows`, its REST API and the event stream) supports two protections, and they can
run **side by side**:

| | Basic Auth | Identity sign-in (OIDC / Keycloak) |
|---|---|---|
| Configure | `FlowDashboard:BasicAuth` | `FlowDashboard:Authorization` + your host's `AddAuthentication` |
| Users | one shared account | your identity provider's users |
| Roles | none — full access | `flow-viewer` (read) / `flow-operator` (read + change) |
| SSO, MFA, audit, logout | no | from the identity provider |
| Good for | scripts, probes, local dev, internal tools behind a VPN | anything people sign in to |

With both on, a request that carries valid Basic credentials gets full access exactly as before, and a
browser without them is redirected to the identity provider. That lets you switch people to SSO without
breaking automation.

Webhook receive endpoints (`POST /flows/api/webhook/{slug}`) are **never** challenged by either; they keep
their webhook secret / HMAC protection ([Webhook hardening](webhook-hardening.md)).

## No authentication configured

With neither Basic Auth nor identity sign-in configured, what an anonymous caller may do depends on the
host environment:

| Host environment | Anonymous callers | Startup log |
|---|---|---|
| `Development` | full operator access (read + change), secrets shown | Warning, EventId `4105` |
| anything else (`Production`, `Staging`, …) | **read-only**: state-changing calls answer `403`, secrets masked as for a viewer | Error, EventId `4103` |
| anything else, with `AllowAnonymousOperators: true` | full operator access, secrets shown | Error, EventId `4104` |

Read-only means exactly what a signed-in `flow-viewer` gets: trigger, rerun, retry, cancel, signal,
enable / disable and schedule changes are refused with `403`, and flow manifests, run trigger bodies and
step inputs / outputs are returned with secret-looking values (webhook secrets, HMAC keys, …) masked. The
page shows an **Anonymous · View only** badge and hides the state-changing buttons.

```json
"FlowDashboard": {
  "AllowAnonymousOperators": true
}
```

> [!WARNING]
> `AllowAnonymousOperators` gives **everyone who can reach the dashboard** operator rights: they can start,
> rerun and cancel flows, deliver signals, change schedules and read webhook secrets. Only set it when
> access is already restricted in front of the dashboard (a private network, an authenticating reverse
> proxy, host-level authorization on the mapped endpoints). Configuring Basic Auth or identity sign-in is
> the better fix.

The environment is read from the host's `IHostEnvironment` (`ASPNETCORE_ENVIRONMENT` /
`DOTNET_ENVIRONMENT`); a host that registers none is treated as non-`Development`.

## Keycloak (OpenID Connect)

The dashboard does not register an authentication handler. You register cookie + OIDC in the host and tell
the dashboard which schemes to use:

```csharp
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie()
    .AddOpenIdConnect("oidc", o =>
    {
        o.Authority = "https://keycloak.example.com/realms/flow-orchestrator";
        o.ClientId = "flow-dashboard";
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.UsePkce = true;
        o.CallbackPath = "/flows/signin-oidc";
        o.SignedOutCallbackPath = "/flows/signout-callback-oidc";
        o.MapInboundClaims = false;
        o.TokenValidationParameters.NameClaimType = "preferred_username";
        o.TokenValidationParameters.RoleClaimType = "roles";
        o.SaveTokens = true;   // sign-out sends id_token_hint; without it Keycloak refuses the logout
    });

// Keycloak nests roles in realm_access / resource_access; map them to role claims.
builder.Services.AddFlowDashboardKeycloakRoles(clientId: "flow-dashboard");

builder.Services.AddFlowDashboard(builder.Configuration);

var app = builder.Build();
app.UseAuthentication();          // handles /flows/signin-oidc
app.MapFlowDashboard("/flows");
```

```json
"FlowDashboard": {
  "Authorization": {
    "AuthenticationScheme": "Cookies",
    "ChallengeScheme": "oidc"
  }
}
```

In Keycloak, create the realm roles `flow-viewer` and `flow-operator` and assign them to users or groups.
Add a *realm roles* protocol mapper with claim name `roles` and **Add to ID token** on, so the cookie carries
the roles. `AddFlowDashboardKeycloakRoles` also understands the `realm_access` / `resource_access` objects
found in access tokens (useful with `AddJwtBearer` for API clients).

### Options

| Key | Default | Meaning |
|---|---|---|
| `Authorization.AuthenticationScheme` | — | Scheme that authenticates a request (usually the cookie scheme). Unset = identity sign-in off. |
| `Authorization.ChallengeScheme` | `AuthenticationScheme` | Scheme that signs a browser in (usually `oidc`). |
| `Authorization.ViewerRoles` | `["flow-viewer"]` | Roles that may read. Operators can always read. `[]` = any signed-in user. |
| `Authorization.OperatorRoles` | `["flow-operator"]` | Roles that may trigger, rerun, retry, cancel, signal, enable / disable and change schedules. |
| `Authorization.ViewPolicy` / `OperatePolicy` | — | Authorization policy names that replace the role checks. |

A misnamed scheme fails at startup (`MapFlowDashboard` throws), not on the first request.

### What users see

- The sidebar shows the signed-in user and a **Sign out** link (`/flows/signout` ends the Keycloak
  session too).
- Viewers get a **View only** badge and no trigger / rerun / retry / cancel / signal / schedule buttons. The
  server enforces the same rule: those endpoints answer `403` for viewers.
- When the session expires, the next API call answers `401` and the page signs in again.
- `GET /flows/api/me` returns `{ method, user, canOperate, canSignOut }` for your own tooling.

## Basic Auth

Unchanged: set both `BasicAuth.Username` and `BasicAuth.Password`. At startup the dashboard now logs a
warning (EventId `4100`) when Basic Auth is the only protection outside the `Development` environment, and
`4101` when the password is a well-known default, shorter than 12 characters, or equal to the user name.

## Local demo

`dotnet run --project FlowOrchestrator.AppHost` starts a Keycloak container on <http://localhost:8088> with
the realm in `FlowOrchestrator.AppHost/keycloak/flow-orchestrator-realm.json` and wires it to the
**PostgreSQL** sample instance (<http://localhost:5102/flows>). Demo users: `viewer` / `viewer-demo-pass`
and `operator` / `operator-demo-pass`. Basic Auth (`admin` / `admin`) still works on that instance, side by
side. The demo realm is for local use only.

## Hardening notes

- **Cross-site requests.** Every state-changing call (`POST` / `PUT`) is rejected with `403` when the
  browser marks it cross-site (`Sec-Fetch-Site` other than `same-origin`) or sends an `Origin` for another
  host. Keep the auth cookie `HttpOnly` and `SameSite=Lax` (or `Strict`); never `SameSite=None`.
- **Secrets.** Viewers get flow manifests, run trigger bodies and headers, step / attempt inputs and
  outputs and the webhook delivery log with every value under a secret-looking name masked (`*secret*`,
  `*password*`, `*pwd*`, `*token*`, `*apikey*`, `*privatekey*`, `*subscriptionkey*`, `*functionskey*`,
  `*accesskey*`, `*authorization*`, `*cookie*`, `*signature*`, HMAC / signing keys, SAS …). Names are
  compared case-insensitively with `-`, `_`, `.` and spaces ignored, so `X-Api-Key`, `api_key` and `apiKey`
  are the same name. Matching is deliberately broad: a harmless name such as `tokenCount` is masked too.
  Operators see everything as stored, so treat the operator role as privileged. Masking is by name — keep
  real secrets out of manifests where you can.
- **Reverse proxies.** Over plain HTTP browsers send no `Sec-Fetch-Site`, so the dashboard compares
  `Origin` with `Host`. If your proxy rewrites `Host` (and ForwardedHeaders middleware does not restore it),
  list the public origin in `FlowDashboard:TrustedOrigins` (e.g. `["http://flows.corp"]`) or the
  dashboard's own trigger / rerun calls get `403`.
- **Client roles.** `AddFlowDashboardKeycloakRoles()` without a `clientId` maps the client roles of
  *every* client in the token; pass the dashboard's client id so another client's `flow-operator` role
  cannot grant access.
- **Policies.** `ViewPolicy` / `OperatePolicy` must be registered with `AddAuthorization`; an unknown name
  fails at startup.
- **Sign-out.** Keycloak only ends its own session when the logout request carries `id_token_hint` (or
  `client_id`, after a confirmation page). Set `SaveTokens = true` on the OIDC handler; otherwise
  `/flows/signout` clears the dashboard cookie, Keycloak answers "Missing parameters: id_token_hint", and
  the next visit signs the user straight back in.
- **Demo realm users** carry `default-roles-<realm>` next to their dashboard role. Without it the Keycloak
  account console (`/realms/<realm>/account`) answers 401, because a user imported with explicit realm
  roles does not get the realm's default roles.
