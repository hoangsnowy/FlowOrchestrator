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
- **Secrets.** Viewers get flow manifests, run trigger bodies and step / attempt inputs and outputs with
  every value under a secret-looking name masked (`*secret*`, `*password*`, `*token*`, `*apikey*`,
  `*authorization*`, `*cookie*`, `*signature*`, HMAC keys …). Operators see them as stored, so treat the
  operator role as privileged. Masking is by name — keep real secrets out of manifests where you can.
- **Reverse proxies.** Over plain HTTP browsers send no `Sec-Fetch-Site`, so the dashboard compares
  `Origin` with `Host`. If your proxy rewrites `Host` (and ForwardedHeaders middleware does not restore it),
  list the public origin in `FlowDashboard:TrustedOrigins` (e.g. `["http://flows.corp"]`) or the
  dashboard's own trigger / rerun calls get `403`.
- **Client roles.** `AddFlowDashboardKeycloakRoles()` without a `clientId` maps the client roles of
  *every* client in the token; pass the dashboard's client id so another client's `flow-operator` role
  cannot grant access.
- **Policies.** `ViewPolicy` / `OperatePolicy` must be registered with `AddAuthorization`; an unknown name
  fails at startup.
