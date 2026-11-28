# Support and Release Policy

This page says which FlowOrchestrator versions get fixes, what each kind of version number promises, and
how to check that a package you downloaded was built from this repository.

## Supported versions

| Line | Runtimes | Status | Gets |
|---|---|---|---|
| **2.x** | .NET 10 (LTS), .NET 11 (STS) | Current | Features, bug fixes, security fixes |
| **1.34.x** | .NET 8, .NET 9, .NET 10 | Maintenance until six months after the 2.0.0 release | Security and critical fixes only |
| 1.33 and older | — | Unsupported | Nothing. Upgrade to 1.34.x or 2.x |

The release dates are on the [GitHub releases page](https://github.com/hoangsnowy/FlowOrchestrator/releases).
1.34.x exists for hosts that cannot move off .NET 8 / .NET 9 yet. .NET 8 and .NET 9 reached end of support on
2026-11-10, so plan the move to .NET 10 and then to 2.x ([Migrating to 2.0](migrating-to-2.0.md)).

## Target frameworks

- Each major version supports the .NET versions that Microsoft supports on the day that major ships.
- A target framework is dropped **only in a major version**, timed to Microsoft's end of support for it.
  2.x targets `net10.0` and `net11.0`; both reach end of support in November 2028.
- A new .NET release is added in a minor version once it has a GA SDK, if every dependency ships for it.

## What a version number promises

FlowOrchestrator follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

| Change | Patch `2.0.x` | Minor `2.x.0` | Major `x.0.0` |
|---|---|---|---|
| Bug and security fixes | ✓ | ✓ | ✓ |
| New API, new options, new storage columns or tables added by the migrator | — | ✓ | ✓ |
| Removed or changed public API | — | — | ✓ |
| Changed default behaviour that hosts can observe | — | only behind an opt-in option | ✓ |
| Dropped target framework | — | — | ✓ |

- **Public API** means every `public` and `protected` member of the `FlowOrchestrator.*` packages.
  Implementation types (stores, dispatchers, hosted services, the engine) are `internal` and may change in
  any release; reach them through `AddFlowOrchestrator(...)` and the interfaces it registers.
- From 2.0.1 on, every package is validated against the shipped 2.0.0 API at pack time, so a patch or a
  minor cannot remove or change a public member by accident.
- **Storage schema** changes are additive within a major and are applied by the built-in migrators at
  startup. A major may need a manual step; its migration guide says so.
- **Persisted work** (queued Hangfire jobs, Service Bus messages, stored runs) written by any version of
  the same major runs on every later version of that major. Crossing a major is covered by its
  migration guide, rehearsed before the release against a running app with live state.

## Deprecation

An API is marked `[Obsolete]` with a diagnostic id for **at least one minor version** before it is removed
in the next major. The message names the replacement.

## Verifying a package

Every package on nuget.org is built and pushed by
[`publish.yml`](https://github.com/hoangsnowy/FlowOrchestrator/blob/main/.github/workflows/publish.yml)
through NuGet Trusted Publishing, with no long-lived API key.

- **Build provenance.** Each `.nupkg` has a signed attestation that binds its bytes to the workflow run
  and the commit it was built from:

  ```bash
  gh attestation verify FlowOrchestrator.Core.2.0.0.nupkg -R hoangsnowy/FlowOrchestrator
  ```

- **SBOM.** Each package contains an SPDX 2.2 software bill of materials at
  `_manifest/spdx_2.2/manifest.spdx.json`.
- **Symbols and source.** Symbol packages are on the nuget.org symbol server, and SourceLink points
  the debugger at the exact commit on GitHub, so you can step into FlowOrchestrator code without a
  local clone.

## Reporting a vulnerability

Do not open a public issue. Use GitHub's
[private vulnerability reporting](https://github.com/hoangsnowy/FlowOrchestrator/security/advisories/new).
Fixes are released on every supported line the vulnerability affects.
