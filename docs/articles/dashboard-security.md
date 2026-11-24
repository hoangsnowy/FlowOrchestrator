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
| `Authorization.OperatorRoles` | `["flow-operator"]` | Roles that may trigger, rerun, retry, cancel, deliver unrestricted signals, enable / disable and change schedules. |
| `Authorization.ViewPolicy` / `OperatePolicy` | — | Authorization policy names that replace the role checks. |
| `Authorization.AdditionalAuthenticationSchemes` | `[]` | Extra schemes tried after `AuthenticationScheme`, e.g. `["Bearer"]` from `AddJwtBearer` for machine clients. Same roles and policies apply. |
| `Authorization.UnregisteredFlowSignals` | `Deny` | Signals for runs of flows this host does not register: `Deny` refuses them for everyone; `OperatorRule` lets operators deliver them (restrictions not enforced). |

A misnamed scheme fails at startup (`MapFlowDashboard` throws), not on the first request.

### API clients (bearer tokens)

Scripts and services that cannot follow a browser sign-in authenticate with an access token. Register a JWT
bearer scheme next to the cookie / OIDC ones and list it in `AdditionalAuthenticationSchemes`:

```csharp
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie()
    .AddOpenIdConnect("oidc", o => { /* as above */ })
    .AddJwtBearer("Bearer", o =>
    {
        o.Authority = "https://keycloak.example.com/realms/flow-orchestrator";
        o.Audience = "flow-dashboard";
        o.MapInboundClaims = false;
        o.TokenValidationParameters.NameClaimType = "preferred_username";
        o.TokenValidationParameters.RoleClaimType = "roles";
    });
builder.Services.AddFlowDashboardKeycloakRoles(clientId: "flow-dashboard");
```

```json
"FlowDashboard": {
  "Authorization": {
    "AuthenticationScheme": "Cookies",
    "ChallengeScheme": "oidc",
    "AdditionalAuthenticationSchemes": [ "Bearer" ]
  }
}
```

A request with a valid cookie never touches the extra schemes. A request that carries an `Authorization`
header other than Basic and that no scheme accepts gets `401` from the bearer handler's challenge (with its
`WWW-Authenticate` header), never a redirect to the identity provider. Bearer schemes take no part in
`/flows/signout`. The roles of a Keycloak service account come from its *Service account roles* tab; a
complete client-credentials walk-through is in
[WaitForSignal → Machine-to-machine](wait-for-signal.md#machine-to-machine-approving-with-a-bearer-token).

### Restricting who may approve a signal

By default delivering a signal is an operator action, so any operator can approve any run. A `WaitForSignal`
step can name its approvers with `allowedRoles` and / or `authorizationPolicy`
([Restricted signals](wait-for-signal.md#restricted-signals)). For such a signal the dashboard:

- requires an identity signed in through `Authorization` (cookie or additional scheme) that satisfies the
  restriction — **the operator role does not bypass it**, and Basic Auth or anonymous callers get `403`;
- lets that approver deliver it **without** a viewer or operator role (only this endpoint; every other route
  keeps the normal rules);
- logs every delivery (EventId `4107`) and refusal (`4108`) with the caller's name.

A caller who is not an operator gets `403` — not `404` — for a run that does not exist, so the signal
endpoint does not reveal which run ids exist.

Restrictions are read from the flows the dashboard host registers. **A signal for a run whose flow that host
does not register is refused for every caller** (`403`, EventId `4108`), restricted or not, because the
dashboard cannot tell. Register the same flows on the dashboard host as on the workers; a split deployment
that cannot may opt back in to the operator rule for such runs, at the cost of not enforcing their
restrictions over HTTP (startup logs EventId `4109`, Warning):

```json
"FlowDashboard": { "Authorization": { "UnregisteredFlowSignals": "OperatorRule" } }
```

### What users see

- The sidebar shows the signed-in user and a **Sign out** link (`/flows/signout` ends the Keycloak
  session too).
- Viewers get a **View only** badge and no trigger / rerun / retry / cancel / signal / schedule buttons. The
  server enforces the same rule: those endpoints answer `403` for viewers. The exception is a restricted
  signal the viewer is an approver for: its **Send signal** button stays; a restricted signal the user may not
  approve shows who may ("Requires role: finance-approver") instead of the button.
- When the session expires, the next API call answers `401` and the page signs in again.
- `GET /flows/api/me` returns `{ method, user, canOperate, canSignOut }` for your own tooling. `method` is
  `none`, `basic`, `oidc` (cookie session) or `bearer` (an `AdditionalAuthenticationSchemes` scheme, which
  has `canSignOut: false`).

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
- **Policies.** `ViewPolicy` / `OperatePolicy` and every step's `authorizationPolicy` must be registered with
  `AddAuthorization`; an unknown name fails at startup.
- **Bearer audience.** Set `Audience` on `AddJwtBearer` and give the API clients' tokens that audience (a
  Keycloak *Audience* mapper); without it any token the realm issues for another client would be accepted.
- **Sign-out.** Keycloak only ends its own session when the logout request carries `id_token_hint` (or
  `client_id`, after a confirmation page). Set `SaveTokens = true` on the OIDC handler; otherwise
  `/flows/signout` clears the dashboard cookie, Keycloak answers "Missing parameters: id_token_hint", and
  the next visit signs the user straight back in.
- **Demo realm users** carry `default-roles-<realm>` next to their dashboard role. Without it the Keycloak
  account console (`/realms/<realm>/account`) answers 401, because a user imported with explicit realm
  roles does not get the realm's default roles.
