# Migrating to 2.0

2.0 is the one breaking-change window after the 1.x line. This guide takes a production deployment
from any 1.x version to 2.0, step by step: what to change in projects and code, how to move a live
Hangfire queue across, which new startup checks may stop the app, and how to verify and roll back.

> **Status.** 2.0 is in pre-release (`2.0.0-alpha.N` on nuget.org). This guide covers everything merged
> so far. Changes still planned before 2.0 GA are listed in
> [Still landing before 2.0 GA](#still-landing-before-20-ga); this page is updated as they land. Track
> progress in the [2.0 roadmap](https://github.com/hoangsnowy/FlowOrchestrator/issues/262).

## At a glance

| # | Change | Who is affected | Effort |
|---|---|---|---|
| 1 | Targets **.NET 10 and .NET 11** only (net8 / net9 dropped) | Everyone | Retarget the app |
| 2 | **`AddFlowOrchestrator()` moved to `FlowOrchestrator.Core`**; Hangfire is an opt-in runtime | Everyone; code change only for non-Hangfire hosts | Package references |
| 3 | **Hangfire step-job format changed**; ≤ 1.16 jobs no longer run; 1.x and 2.0 workers must not share Hangfire storage | Hangfire hosts | Planned rollout (drain) |
| 4 | **Fail-fast startup checks**: no runtime, missing stores, dotted / empty step keys, duplicate flow ids | Anyone whose setup relied on silent fallbacks | Fix configuration |
| 5 | **Dashboard is read-only without authentication outside `Development`** (shipped in 1.34.2) | Hosts that never configured dashboard auth | Configure auth or opt in |
| 6 | **Step inputs resolved in a single pass** — substituted trigger / step values are never re-evaluated (shipped in 1.34.2) | Practically no one (it was a vulnerability) | None |
| 7 | **Removed APIs**: `IFlowRepository`, `IFlowExecutor`, legacy sequential mode, … | Custom storage backends, hand-built engines, custom `IStepInstance` / `IOutputsRepository` | Code changes |
| 8 | Untyped handlers on Hangfire no longer get Newtonsoft-restored POCOs | Hangfire hosts with untyped (`IStepHandler` without `<TInput>`) handlers that cast inputs to POCOs | Check casts |
| 9 | **Persisted run events removed** (`FlowEvents`, `EnableEventPersistence`, `IFlowEventReader`); the dashboard Events drawer and `FlowTestRunResult.Events` now derive the timeline from step attempts | Hosts that set `EnableEventPersistence`, custom storage backends, anything reading `FlowEvents` directly | Delete code; optional manual `DROP TABLE` |

If you only use the built-in pieces (a flow manifest, `IStepHandler<TInput>` handlers, one of the
built-in storage backends and runtimes, the dashboard), most of 2.0 is **items 1–3 plus a configuration
review**.

---

## Recommended upgrade path

```text
any 1.x  ──►  1.34.3  ──(run it in production for a while)──►  2.0
```

1. **Upgrade to 1.34.3 first**, on your current .NET 8 / 9 / 10 runtime. 1.34.3 is the last 1.x release
   and carries every fix that 2.0 has, including three security fixes
   ([GHSA-g5jp-pqw5-wrmj](https://github.com/hoangsnowy/FlowOrchestrator/security/advisories/GHSA-g5jp-pqw5-wrmj),
   [GHSA-358q-p9gq-3m6p](https://github.com/hoangsnowy/FlowOrchestrator/security/advisories/GHSA-358q-p9gq-3m6p),
   [GHSA-8248-pg9c-cp7c](https://github.com/hoangsnowy/FlowOrchestrator/security/advisories/GHSA-8248-pg9c-cp7c))
   and the SQL Server execute-once / concurrency fixes. Its two behaviour changes — the read-only
   unauthenticated dashboard and single-pass input resolution — then surface **separately** from the
   2.0 changes, which makes any problem much easier to attribute.
2. **Then move to 2.0** with this guide. Coming from 1.34.3, the remaining work is the .NET retarget,
   package references, the Hangfire queue rollout and the new startup checks.

Upgrading straight from an old 1.x to 2.0 works too; do every step below, plus the 1.34.2 items in
[Step 7](#step-7--review-the-behaviour-changes).

---

## Step 0 — Before you start

- [ ] **Read the release notes** for 1.34.2, 1.34.3 and 2.0 (`CHANGELOG.md`).
- [ ] **Back up the FlowOrchestrator database** (SQL Server / Azure SQL / PostgreSQL). So far 2.0 changes
      nothing in an existing database (see [Step 8](#step-8--database)), but take the backup anyway; you
      will want it for [rollback](#rollback).
- [ ] **Inventory your extension points.** Search your solution for these; each one has a section below:

  ```text
  IFlowRepository  FlowRepositoryExtensions  IFlowExecutor  FlowExecutor  new FlowOrchestratorEngine(
  IFlowRunStore  IFlowRunRuntimeStore  IFlowRunControlStore  IOutputsRepository  IStepInstance
  IRecurringTriggerSync  SyncTriggers(  FlowOrchestratorOptions  AddFlowOrchestratorSqlServer
  AddFlowOrchestratorPostgreSql  ScopeMoveNext  EndScopeAsync  AllKnownStepKeys  FindParentStep
  FindNextStep  ShouldExecute  HangfireEnabled  FlowSignalDispatcher(  EnableEventPersistence
  IFlowEventReader  RecordEventAsync  GetRunEventsAsync  FlowEvent  FlowEvents  flow_events
  ```

  No hits means you use only the built-ins and can skip [Step 6](#step-6--update-custom-extensions).
- [ ] **Check your flow manifests** for top-level step keys that contain `.` or are empty
      ([Step 5](#step-5--fix-what-the-new-startup-checks-report)).
- [ ] **Know which runtime you run**: Hangfire, InMemory or Azure Service Bus. The package and rollout
      steps differ per runtime.

---

## Step 1 — Retarget to .NET 10 or .NET 11

Every 2.0 package ships `net10.0` and `net11.0` only. .NET 8 and .NET 9 reached end of support on
2026-11-10, and the current Npgsql (11), OpenTelemetry (1.21) and `Microsoft.Extensions.*` (11) releases
no longer build for them. Hosts that must stay on .NET 8 / 9 stay on **1.34.x**.

**Which one?** `net10.0` is the LTS release (supported until November 2028) and the safer default for
production. `net11.0` is STS; choose it if you already standardise on 11.

```xml
<!-- your host .csproj -->
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>   <!-- was net8.0 / net9.0 -->
</PropertyGroup>
```

Also update, where you have them:

- [ ] `global.json` — an SDK that can build your target (`10.0.x` for net10.0, `11.0.x` for net11.0).
- [ ] Docker base images — `mcr.microsoft.com/dotnet/aspnet:10.0` (or `:11.0`) and the matching `sdk` image.
- [ ] CI — `actions/setup-dotnet` versions, build agents, any `-f net8.0` arguments.
- [ ] Azure App Service / container app runtime stack.
- [ ] Other packages pinned to `8.*` / `9.*` versions of `Microsoft.Extensions.*` or
      `Microsoft.AspNetCore.*`: FlowOrchestrator 2.0 depends on `Microsoft.Extensions.*` **11.0.0**
      (also on net10.0 — Npgsql 11 requires it), so lower pins now produce `NU1605` / `NU1109` downgrade
      errors. Raise them.

Build once at this point; fix anything the retarget itself breaks before touching FlowOrchestrator.

---

## Step 2 — Update package references

`AddFlowOrchestrator()` moved from `FlowOrchestrator.Hangfire` to **`FlowOrchestrator.Core`**, into the
`Microsoft.Extensions.DependencyInjection` namespace (in scope without a `using`). `SqlServer`,
`PostgreSQL`, `ServiceBus` and `Testing` no longer depend on `FlowOrchestrator.Hangfire`, and Core no
longer depends on `Newtonsoft.Json`. Reference only what your runtime needs:

| Runtime | FlowOrchestrator packages | Also needed | Hangfire / Newtonsoft.Json in your graph |
|---|---|---|---|
| **Hangfire** | `Core`, `Hangfire`, a storage package (`SqlServer` or `PostgreSQL`) | `Hangfire.AspNetCore` + a Hangfire storage package (e.g. `Hangfire.SqlServer`) | Yes — via `Hangfire.Core` |
| **InMemory** | `Core`, `InMemory` (optionally `SqlServer` / `PostgreSQL` for storage) | — | **No** |
| **Azure Service Bus** | `Core`, `ServiceBus`, a storage package (`SqlServer` or `PostgreSQL`) | — | **No** |
| any | `Dashboard`, `OpenTelemetry`, `Testing` as before | — | No |

### Hangfire host — before / after

```xml
<!-- 1.x -->
<PackageReference Include="FlowOrchestrator.Hangfire"  Version="1.34.3" />
<PackageReference Include="FlowOrchestrator.SqlServer" Version="1.34.3" />

<!-- 2.0: keep Hangfire; referencing Core explicitly is optional (Hangfire still depends on it) but
     makes the AddFlowOrchestrator() origin visible -->
<PackageReference Include="FlowOrchestrator.Core"      Version="2.0.0" />
<PackageReference Include="FlowOrchestrator.Hangfire"  Version="2.0.0" />
<PackageReference Include="FlowOrchestrator.SqlServer" Version="2.0.0" />
```

### InMemory or Service Bus host — before / after

```xml
<!-- 1.x: FlowOrchestrator.Hangfire was required just for AddFlowOrchestrator() -->
<PackageReference Include="FlowOrchestrator.Hangfire"   Version="1.34.3" />
<PackageReference Include="FlowOrchestrator.ServiceBus" Version="1.34.3" />
<PackageReference Include="FlowOrchestrator.SqlServer"  Version="1.34.3" />

<!-- 2.0: drop FlowOrchestrator.Hangfire entirely -->
<PackageReference Include="FlowOrchestrator.Core"       Version="2.0.0" />
<PackageReference Include="FlowOrchestrator.ServiceBus" Version="2.0.0" />
<PackageReference Include="FlowOrchestrator.SqlServer"  Version="2.0.0" />
```

> Using Central Package Management? Change the versions in `Directory.Packages.props` and add a
> `<PackageVersion Include="FlowOrchestrator.Core" … />` entry.

**Verify** that a non-Hangfire host no longer carries Hangfire:

```bash
dotnet list YourHost.csproj package --include-transitive | grep -iE "hangfire|newtonsoft"
# InMemory / Service Bus hosts: no output expected (unless your own code references them)
```

---

## Step 3 — Update the host code

### Hangfire host: no code change

The registration compiles unchanged. `UseHangfire()` is now an extension method in the
`FlowOrchestrator.Hangfire` namespace (it used to be a method on the builder), so keep that `using`:

```csharp
using FlowOrchestrator.Hangfire;   // keep: UseHangfire() lives here
using FlowOrchestrator.SqlServer;
using Hangfire;

builder.Services.AddHangfire(c => c.UseSqlServerStorage(connectionString)); // unchanged
builder.Services.AddHangfireServer();                                        // unchanged

builder.Services.AddFlowOrchestrator(options =>   // now from FlowOrchestrator.Core
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();
    options.AddFlow<OrderFulfillmentFlow>();
});
```

### InMemory host: remove the Hangfire leftovers

```csharp
// 1.x — Hangfire was wired only because AddFlowOrchestrator lived in that package
using FlowOrchestrator.Hangfire;                      // ← remove
builder.Services.AddHangfire(c => c.UseInMemoryStorage()); // ← remove if it existed only for this
builder.Services.AddFlowOrchestrator(options =>
{
    options.UseInMemory();
    options.UseInMemoryRuntime();
    options.AddFlow<MyFlow>();
});
```

### Azure Service Bus host: remove the Hangfire leftovers

```csharp
using FlowOrchestrator.ServiceBus;
using FlowOrchestrator.SqlServer;
// using FlowOrchestrator.Hangfire;   ← remove

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseAzureServiceBusRuntime(sb =>
    {
        sb.ConnectionString = serviceBusConnectionString;
    });
    options.AddFlow<MyFlow>();
});
```

### Exactly one runtime

`AddFlowOrchestrator()` now **throws at startup when no runtime is selected** (see
[Step 5](#step-5--fix-what-the-new-startup-checks-report)). Runtime registrations use `TryAdd`, so if
the callback selects two runtimes, **the first one wins**. Select exactly one of `UseHangfire()`,
`UseInMemoryRuntime()` or `UseAzureServiceBusRuntime(...)`.

---

## Step 4 — Move the Hangfire queue across (Hangfire hosts only)

Two facts drive this step:

1. **Step jobs have a new shape.** A 2.0 step job carries one `HangfireStepEnvelope` (flow id, run id,
   principal, step key / type / index, scheduled time, inputs as a System.Text.Json string). Trigger
   body and headers are no longer copied into each job; the engine re-reads them from the run's stored
   trigger data.
2. **Old payloads, by version:**

   | Job enqueued by | Runs on a 2.0 worker? | Runs on a 1.x worker? |
   |---|---|---|
   | FlowOrchestrator **≤ 1.16** | **No** — the target method is removed | Yes |
   | FlowOrchestrator **1.17 – 1.34.x** | **Yes** — the overload is kept, `[Obsolete]`, for one major | Yes |
   | FlowOrchestrator **2.0** | Yes | **No** — a 1.x worker cannot run a 2.0 job |

   So: drain anything enqueued by ≤ 1.16, and never let 1.x and 2.0 workers process the same Hangfire
   storage at the same time.

### 4.1 Check for ≤ 1.16 jobs

If you upgraded to 1.34.3 first and it has run for a while, every job created since is 1.17+ already.
Still, check the queue before the cut-over. With the default `HangFire` schema on SQL Server:

```sql
-- Live jobs whose arguments still use the ≤ 1.16 signature (they carry the flow definition type)
SELECT j.Id, j.StateName, j.CreatedAt
FROM HangFire.Job AS j
WHERE j.StateName IN ('Enqueued', 'Scheduled', 'Processing', 'Awaiting')
  AND j.InvocationData LIKE '%IFlowDefinition%';
```

On PostgreSQL storage for Hangfire (`Hangfire.PostgreSql`), run the same filter against its job table —
by default `hangfire.job` with columns `invocationdata` and `statename`; check your configured schema
name. **Zero rows** means nothing blocks the upgrade. Any rows: let them
finish on a 1.x worker first (they are usually long-scheduled polling steps or `WaitForSignal`
re-checks), or re-trigger those runs after the upgrade.

### 4.2 Cut over (pick one)

**A — Short maintenance window (simplest, recommended):**

1. Stop accepting new triggers: pause cron triggers from the dashboard, and hold webhook traffic at your
   gateway (or accept that callers retry).
2. Wait until the Hangfire dashboard shows **0 Enqueued and 0 Processing** for the FlowOrchestrator
   queues. Scheduled jobs (polling re-checks, delays) may remain: they are 1.17+ jobs and a 2.0 worker
   runs them (verify with the query in 4.1 that none is ≤ 1.16).
3. **Stop every 1.x instance.**
4. Deploy 2.0 to every instance and start them.
5. Resume cron triggers and webhook traffic.

**B — Blue/green with separate Hangfire storage:** stand 2.0 up against a *new* Hangfire storage (a new
schema, `HangFire2`, or a separate database for Hangfire only — the FlowOrchestrator tables stay shared),
switch triggers and webhooks to it, and let the 1.x deployment drain its own queue before you retire it.
Runs started on 1.x finish on 1.x; new runs start on 2.0.

**Cron triggers fire once per deployment** while both are up: each deployment registers the recurring
jobs in its own Hangfire storage, and both read the same FlowOrchestrator tables. Before you start the
2.0 deployment, stop the 1.x side from firing schedules — run its Hangfire server without the recurring
job scheduler, or delete its `flow-*` recurring jobs from the 1.x Hangfire dashboard. Do **not** pause
the flows from either FlowOrchestrator dashboard for this: pause state lives in the shared database, so
it pauses the schedule in both deployments.

**Do not** do a rolling deployment that leaves 1.x and 2.0 instances consuming the **same** Hangfire
storage: a 1.x worker that picks up a 2.0 job fails it.

### 4.3 Inputs of untyped handlers

Step inputs now travel in the Hangfire job as JSON and reach handlers as JSON-shaped CLR values:
`string`, `long`, `double` (or `decimal` when `double` would drop digits the payload carried), `bool`,
`Dictionary<string, object?>`, `List<object?>`. Typed handlers (`IStepHandler<TInput>`) bind exactly as
before.

What changes for **untyped** handlers
(`ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)`) is objects: 1.x
Hangfire jobs serialised inputs with Newtonsoft `TypeNameHandling`, so a POCO or typed collection placed
in the inputs came back as that type. In 2.0 it arrives as a `Dictionary<string, object?>`:

```csharp
// before (Hangfire 1.x restored the POCO from "$type")
var address = (Address)step.Inputs["address"]!;

// after: bind it yourself, or move the handler to IStepHandler<TInput>
var address = JsonSerializer.Deserialize<Address>(JsonSerializer.Serialize(step.Inputs["address"]))!;
```

Numbers are unaffected: Hangfire 1.x already returned JSON integers as `long` and reals as `double`.

> The three runtimes do not hand untyped handlers the same shapes today: InMemory passes the original
> CLR objects, Service Bus passes `JsonElement`, Hangfire passes the shapes above. A handler that must
> run on more than one runtime should be typed (`IStepHandler<TInput>`), which binds the same way on all
> three. Unifying the untyped shape is planned for 2.0 GA — see
> [Still landing before 2.0 GA](#still-landing-before-20-ga).

---

## Step 5 — Fix what the new startup checks report

1.x quietly degraded in several situations; 2.0 refuses to start instead. The runtime and storage
checks throw from the `AddFlowOrchestrator(...)` call itself; the store checks throw when the engine is
first resolved; the step-key and flow-id checks throw when the flow-sync hosted service starts. Either
way a misconfigured deployment fails at startup instead of misbehaving later. Message texts are quoted exactly so you can search logs.

| You see at startup | Cause | Fix |
|---|---|---|
| `No FlowOrchestrator runtime registered. Call options.UseHangfire() (package FlowOrchestrator.Hangfire), options.UseInMemoryRuntime() (package FlowOrchestrator.InMemory), or options.UseAzureServiceBusRuntime(...) (package FlowOrchestrator.ServiceBus) …` | No runtime selected. 1.x silently wired a Hangfire dispatcher that could not run without `UseHangfire()` | Call exactly one of the three |
| `The configured FlowOrchestrator runtime registered an IStepDispatcher but no IRecurringTriggerDispatcher. …` | A **custom** runtime registers a dispatcher but no recurring dispatcher / inspector | Register `IRecurringTriggerDispatcher` and `IRecurringTriggerInspector` too |
| `No FlowOrchestrator storage backend registered. Call options.UseSqlServer(), options.UsePostgreSql(), or options.UseInMemory() inside AddFlowOrchestrator(options => ...).` | No storage selected | Call one storage method, or register a custom backend ([Step 6](#custom-storage-backend)) |
| `… requires an IFlowRunRuntimeStore registration, but none was found. …` (named component: `FlowOrchestratorEngine`, `FlowSignalDispatcher` or `FlowRunRecoveryHostedService`) | A custom storage backend registers `IFlowRunStore` but not the runtime store. 1.x fell back to a legacy sequential mode with no claims and no recovery | Register your store as `IFlowRunRuntimeStore` too ([Step 6](#custom-storage-backend)) |
| `FlowOrchestratorEngine requires an IFlowRunControlStore registration, …` | Same, for the run-control store (cancel, timeout, idempotency) | Register it as `IFlowRunControlStore` |
| `Step key '…' contains '.', which is reserved as the runtime scope separator.` | A top-level step key contains `.`; runtime keys are `.`-joined scope paths (`loop.3.child`), so such a key was ambiguous | Rename the step (e.g. `send.email` → `send_email`), and update every `runAfter` / `@steps('…')` that references it. **Run history keeps the old key**: runs started before the rename show it under the old name |
| `Flow has a step with an empty key.` | A step registered under `""` | Give it a key |
| `Step keys '…' and '…' differ only by case; step keys must be unique ignoring case within a scope, because SQL Server compares them case-insensitively.` | Two steps in the same scope (top level, or the children of one `ForEach`) whose keys differ only by case. On SQL Server they shared one claim / dispatch / status row and the run never finished; the check applies to every provider so a flow behaves the same everywhere | Rename one of them, and update every `runAfter` / `@steps('…')` that references it. Run history keeps the old key |
| `Step key '…' contains ':', which is reserved as the outputs-key separator ("{stepKey}:input").` | A step key (at any level) contains `:`. Resolved inputs are stored under `"{stepKey}:input"` and trigger data under `"__trigger:data"`, so such a key could overwrite another step's inputs | Rename the step (e.g. `crm:sync` → `crm_sync`) and its references |
| `Step key '…' is N characters long; the maximum is 250 (the stores persist "{stepKey}:input" in a 256-character key column).` | A step key's flattened path (`loop.child`) exceeds `FlowGraphPlanner.MaxStepKeyLength`. SQL Server / PostgreSQL failed the first write of such a step with a truncation error | Shorten the key. Keys inside a `ForEach` also carry the iteration index at runtime (`loop.12.child`), so keep them well below the limit |
| `Flows '…' and '…' are both registered with id '…'. Flow ids must be unique: …` | Two **different** flow types share an `Id` (usually a copy-pasted `Guid`). In 1.x one silently shadowed the other | Give each flow its own stable `Id`. Registering the *same* type twice stays harmless |

> Changing a flow's `Id` makes it a new flow to the store: its run history, schedule overrides and
> enabled flag stay with the old id. Change the id of the flow that was being shadowed, not the one
> whose history you want to keep.

---

## Step 6 — Update custom extensions

Skip this step if the [Step 0 inventory](#step-0--before-you-start) found nothing.

### Removed and changed API

| 1.x | 2.0 | What to do |
|---|---|---|
| `IFlowRepository`, `FlowRepositoryExtensions.FindById`, `InMemoryFlowRepository` | **`FlowRegistry`** (`FlowOrchestrator.Core.Abstractions`) | Inject `FlowRegistry`; use `TryGetFlow(id, out var flow)` / `Find(id)` / `Flows`. Do not register a flow repository in custom backends |
| `IFlowExecutor`, `FlowExecutor` | removed (never called) | Delete registrations and constructor arguments |
| `FlowOrchestratorEngine(…, IFlowExecutor, …, IFlowEventNotifier? eventNotifier = null)` | no executor parameter; `eventNotifier` **required** | Hand-built engines: drop the executor, pass `NoopFlowEventNotifier.Instance` |
| `FlowSignalDispatcher(…, telemetry, …)` | takes `IEnumerable<IFlowRunRuntimeStore> runtimeStores` **before** the optional `telemetry` | Callers passing arguments by position reorder them |
| `IRecurringTriggerSync.SyncTriggers(Guid, bool)` | `SyncTriggersAsync(Guid, bool, CancellationToken)` | `await` it; one Core implementation (`RecurringTriggerSync`) serves every runtime |
| hand-built `$"flow-{id}-{key}"` job ids | **`RecurringTriggerJobId`** | Build and parse with it |
| `EphemeralFlowScheduleStateStore` (Hangfire), `InMemory.InMemoryFlowScheduleStateStore` | `FlowOrchestrator.Core.Storage.InMemoryFlowScheduleStateStore` | Update the namespace; it now stores and returns copies |
| `FlowOrchestratorBuilder.HangfireEnabled`, `builder.UseHangfire()` (instance method) | `UseHangfire()` extension in `FlowOrchestrator.Hangfire` | Keep `using FlowOrchestrator.Hangfire;` |
| `AddFlowOrchestratorSqlServer(services, cs)`, `AddFlowOrchestratorPostgreSql(services, cs)` | removed (duplicates) | `AddFlowOrchestrator(o => o.UseSqlServer(cs))` or `new FlowOrchestratorBuilder(services).UseSqlServer(cs)` |
| `FlowOrchestratorOptions` (`ConnectionString`) | removed (never read) | Delete |
| `IOutputsRepository.EndScopeAsync` | removed | Delete your override |
| `IStepInstance.ScopeMoveNext` | removed | Delete the property from custom `IStepInstance` implementations |
| `StepMetadata.ShouldExecute`, `StepCollection.FindNextStep` | removed (legacy sequential mode) | Use the DAG planner's evaluation; there is no per-successor API any more |
| `StepCollection.FindParentStep` | removed | Walk `IScopedStep.Steps` yourself if you need it |
| `FlowGraphEvaluation.AllKnownStepKeys` | removed | Use `ReadyStepKeys` / `BlockedStepKeys` / `WaitingStepKeys` |
| `IHangfireStepRunner.RunStepAsync(ctx, IFlowDefinition, step)` | removed (≤ 1.16 jobs) | See [Step 4](#step-4--move-the-hangfire-queue-across-hangfire-hosts-only) |
| `FlowSyncHostedService`, `FlowRetentionHostedService` (internal, in the Hangfire package) | internal, in `FlowOrchestrator.Core.Hosting` | Registered by `AddFlowOrchestrator()`; do not register them yourself |
| `FlowObservabilityOptions.EnableEventPersistence` | removed (run events are no longer persisted) | Delete the assignment |
| `IOutputsRepository.RecordEventAsync`, `FlowOrchestrator.Core.Storage.FlowEvent` | removed | Delete your implementation |
| `IFlowEventReader.GetRunEventsAsync` | removed; **`FlowRunTimeline.Build(run)`** (`FlowOrchestrator.Core.Storage`) derives the same `FlowEventRecord` list from `IFlowRunStore.GetRunDetailAsync(runId)` | Call it instead; delete your reader implementation and its registration |
| `LogEvents.EventPersistenceFailed` (EventId 3002) | removed; 3002 is retired | Drop alert rules on 3002 |
| direct queries on `FlowEvents` / `flow_events` | the table is no longer written | Read `GET {base}/api/runs/{runId}/events`, `FlowStepAttempts`, or OpenTelemetry spans |
| `IFlowOrchestrator.RetryStepAsync` returned the step's result after running it inline | returns a **`StepRetryResult`** (`Accepted` / `NotFailed` / `Conflict`) once the new attempt is dispatched | Do not read a step output from the return value; wait for the run (see [Step 7](#manual-retry-is-dispatched-and-accepts-only-a-failed-step)) |
| `IFlowRunStore.RetryStepAsync` → `Task` | `Task<bool>`: resets only a `Failed` step, atomically | Custom stores: see the checklist below |

### Custom storage backend

A complete 2.0 backend registers, for the same store instance:

```csharp
services.AddSingleton<MyRunStore>();
services.AddSingleton<IFlowRunStore>(sp => sp.GetRequiredService<MyRunStore>());
services.AddSingleton<IFlowRunRuntimeStore>(sp => sp.GetRequiredService<MyRunStore>()); // required since 2.0
services.AddSingleton<IFlowRunControlStore>(sp => sp.GetRequiredService<MyRunStore>()); // required since 2.0
services.AddSingleton<IFlowStore, MyFlowStore>();
services.AddSingleton<IOutputsRepository, MyOutputsRepository>();
// no IFlowRepository any more — FlowRegistry is built from AddFlow<T>() by Core
// no IFlowEventReader / RecordEventAsync any more — the run timeline is derived from step attempts
```

If your backend stored run events, delete `RecordEventAsync`, the `IFlowEventReader` implementation and
registration, and the event-row delete in `CleanupAsync`. The dashboard's Events drawer now reads
`GetRunDetailAsync`, so make sure it returns `FlowStepRecord.Attempts` (one entry per execution, with
start / end / status / error) — that is what the timeline is built from.

Then check your implementation against the 1.34.3 storage fixes, which define the contract precisely:

- [ ] **`ResetCascadeSkippedDependentsAsync`** clears claim and dispatch rows **only** for keys whose
      step row was a `PrerequisitesUnmet` skip that this call reset — never for succeeded or
      `When`-skipped steps. Otherwise a retried run can execute an already-succeeded step again.
- [ ] **Insert-if-absent operations** (`TryClaimStepAsync`, `TryRecordDispatchAsync`,
      `TryRegisterIdempotencyKeyAsync`, webhook nonces) are atomic under concurrency and return `false`
      for the loser — never throw a duplicate-key error. On SQL Server use
      `WITH (UPDLOCK, HOLDLOCK)`; on PostgreSQL `INSERT … ON CONFLICT DO NOTHING`.
- [ ] Trigger headers read back from storage use a case-insensitive dictionary
      (`StringComparer.OrdinalIgnoreCase`), and are a snapshot taken at save time, not the caller's
      dictionary.
- [ ] **Idempotency keys** go through `IdempotencyKeyStorage.Normalize` before they are stored or looked up
      (trim, lower-case, and a `sha256:` digest for keys over 256 characters), and the copy passed to
      `ConfigureRunAsync` through `IdempotencyKeyStorage.ToStorageForm`. That is what the built-in stores
      do, so a key deduplicates the same way whichever backend you move to.
- [ ] **`MarkTimedOutAsync`** returns `false` without writing when the timeout is already latched **or a
      cancel was already requested** (a user cancel wins), and keeps an existing `CancelReason` when
      `reason` is `null`. **`ConfigureRunAsync`** is an upsert that keeps the cancel / timeout fields of an
      existing record.
- [ ] Signal names and run-status filters compare case-insensitively (`RunStatusNames.Canonicalize` maps a
      status filter onto the persisted spelling without giving up an index).
- [ ] The conditional transitions added in 2.0 have **no default implementation**, and each must be one
      atomic step (a conditional `UPDATE` and its row count, inside one transaction):
      - `IFlowRunStore.TryCompleteRunningStepAsync` — completes a step only while it is `Running`
        (the `ForEach` settle);
      - `IFlowRunStore.RetryStepAsync` — `Failed` → `Running`, run back to `Running`, claim and ledger
        rows deleted, or nothing at all;
      - `IFlowRunRuntimeStore.ReleaseOrphanedClaimsAsync` — deletes claims with no step row older than
        the given age (on the clock that stamped them) together with their ledger rows.
- [ ] `RecordSkippedStepAsync` writes the step row and its attempt row in one transaction.
- [ ] Optional members with default implementations (`IsStepClaimedAsync`, `IsStepDispatchedAsync`,
      `GetStepAsync`, `SearchRunsAsync`, …) are worth overriding with point queries: the defaults fetch
      whole sets.

### Hand-built engines and tests

If tests or tools construct `FlowOrchestratorEngine` directly, update the constructor call (no
`IFlowExecutor`, a `FlowRegistry` instead of `IFlowRepository`, a non-null `IFlowEventNotifier`, and
non-empty runtime and control store sequences). Prefer `FlowOrchestrator.Testing`'s `FlowTestHost`,
which builds the engine through `AddFlowOrchestrator()` and no longer needs Hangfire.

---

## Step 7 — Review the behaviour changes

### Dashboard without authentication is read-only outside `Development` (since 1.34.2)

When neither `FlowDashboard:BasicAuth` nor `FlowDashboard:Authorization` is configured and the host does
not run in `Development`:

- every state-changing call (trigger, re-run, retry, cancel, signal, enable / disable, cron changes)
  answers **`403`**;
- webhook secrets, HMAC keys and secret-looking payload values are **masked**;
- startup logs EventId **4103** (Error).

Fix it properly by configuring authentication — see [Securing the dashboard](dashboard-security.md):

```json
{
  "FlowDashboard": {
    "BasicAuth": { "Username": "ops", "Password": "<from a secret store>" }
  }
}
```

If access is already restricted in front of the dashboard (private network, gateway auth) and you want
the previous behaviour, opt in explicitly — startup then logs EventId 4104 (Error) so the decision stays
visible:

```json
{ "FlowDashboard": { "AllowAnonymousOperators": true } }
```

`Development` hosts are unchanged (EventId 4105, Warning).

### Signal endpoint: per-signal authorization and bearer tokens

New and opt-in: a `WaitForSignal` step can restrict who may deliver its signal (`allowedRoles` /
`authorizationPolicy`), and `FlowDashboard:Authorization:AdditionalAuthenticationSchemes` lets API clients
sign in with a bearer token — see [WaitForSignal → Security](wait-for-signal.md#security). Existing flows and
configuration behave as before, except:

- **a signal for a run whose flow the dashboard host does not register is refused with `403` for every
  caller** (EventId `4108`), because the dashboard cannot read the step's restriction without the manifest.
  Hosts that register every flow with `AddFlow` are unaffected. In a split deployment whose dashboard host
  has no flows, register the same flows there, or restore the 1.x operator rule explicitly (startup then logs
  EventId `4109`, Warning):

  ```json
  { "FlowDashboard": { "Authorization": { "UnregisteredFlowSignals": "OperatorRule" } } }
  ```

- `GET …/api/me` reports a caller signed in through an additional (bearer) scheme as `method: "bearer"`,
  `canSignOut: false`;
- a request with an `Authorization` header other than Basic that no scheme accepts answers **`401`** instead
  of redirecting to the identity provider, page requests included;
- `POST …/signals/{name}` checks the run before the body: a missing run answers `404` to operators and `403`
  to everyone else even when the body is not valid JSON (it used to be `400`).

### Step inputs are resolved in a single pass (since 1.34.2)

Each manifest input is classified once from the manifest's own text. A value substituted from the
trigger or from a step output is passed to the handler **verbatim**, never evaluated again as an
expression. Expressions written in the manifest resolve exactly as before. The only flows that change
are ones that (knowingly or not) let a trigger payload inject `@steps(…)` / `@triggerBody()`
expressions — the vulnerability fixed by GHSA-8248-pg9c-cp7c.

### Webhook security now enforces by default

`WebhookSecurityOptions.EnforcementMode` defaults to `Enforce` (1.x: `Off`). Under the old default the
pipeline accepted every request before any gate ran, so the gates a trigger declared — signature scheme
and HMAC key, IP allow / deny, rate limit, replay tolerance — were not applied. After upgrading they are.

- A trigger with no gate inputs behaves exactly as before.
- A trigger with gate inputs whose senders do not satisfy them now gets `401` (signature), `403` (IP),
  `429` (rate limit) or `409` (replay) instead of `200`.

Before upgrading, check `/flows/api/webhooks/stats` or the DLQ under `Audit` for a release cycle, or set
the mode explicitly to keep the old behaviour while you fix senders:

```csharp
builder.Services.AddFlowDashboard(builder.Configuration, opts => opts.UseWebhookSecurity(sec =>
    sec.UseEnforcementMode(WebhookEnforcementMode.Audit)));   // or .Off
```

### Manual retry is dispatched, and accepts only a failed step

The dashboard Retry button (`POST {base}/api/runs/{runId}/steps/{stepKey}/retry`) and
`IFlowOrchestrator.RetryStepAsync` used to run the step's handler inline, inside the HTTP request, while
the response said "retry enqueued". Two concurrent retries could also run the handler twice at once. Now:

- the retry resets the step with a compare-and-swap on `Failed` and **dispatches** the new attempt through
  the runtime; the call returns as soon as it is queued, and the result arrives through the run like any
  other attempt;
- **only a `Failed` step can be retried.** A `Succeeded`, `Skipped` (including one skipped because the run
  was cancelled or timed out) or still-running step is refused: the endpoint answers `400`, the engine
  returns `StepRetryOutcome.NotFailed`, and nothing changes. 1.x allowed the engine call on any step;
- of two concurrent retries, one is accepted and the other gets **`409`** (`StepRetryOutcome.Conflict`).

The response body for an accepted retry is unchanged. If a script read the step's outcome right after the
retry call, poll `GET {base}/api/runs/{runId}` instead.

### Smaller changes

- A step whose start cannot be recorded (the run-control read or the start write throws) is no longer run
  anyway: the engine releases its claim and rethrows, so the runtime retries the job. Startup recovery and
  the timeout sweep release a claim left without a step row by a crashed worker once it is older than
  `FlowRunControlOptions.OrphanedClaimTimeout` (default 60 s; `null` turns it off).
- A step whose dispatch-ledger row was written but whose send never returned (a replica killed mid-send, a
  send hung in the broker client) is re-delivered by the new `FlowUnsentDispatchRecoveryHostedService` once
  the row is older than `FlowRunControlOptions.UnsentDispatchTimeout` (default 60 s; `null` turns it off).
  The SQL Server / PostgreSQL migrator adds one filtered index on the dispatch ledger for it. A custom
  `IFlowRunStore` opts in by implementing `LeaseUnsentDispatchesAsync`; the default does nothing.
- `@triggerHeaders()['name']` is case-insensitive in every step on SQL Server / PostgreSQL storage
  (1.34.3); it used to be case-sensitive after the entry step. InMemory storage now does the same even
  when the trigger was raised with an ordinal dictionary.
- **PostgreSQL:** signal names (`POST {base}/api/runs/{runId}/signals/{name}`) and the run-list status
  filter (`?status=failed`) are case-insensitive, as they already were on SQL Server and InMemory. A
  duplicate signal delivery that races the first one now gets `409` (already delivered) instead of `404`.
- An idempotency key longer than 256 characters is accepted on SQL Server / PostgreSQL (it returned `500`)
  and stored as a `sha256:` digest; shorter keys are stored exactly as before.
- A run the user cancelled stays `Cancelled` even when its deadline passes while the cancel is being
  recorded; it used to be relabelled `TimedOut`, and a later retry could then clear the cancel.
- The Hangfire package no longer brings the OpenTelemetry SDK or `Microsoft.Extensions.Logging`
  transitively. Hosts on the generic host are unaffected; a bare `new ServiceCollection().AddLogging()`
  needs a direct `Microsoft.Extensions.Logging` reference.
- New hosting log events 5000–5005 (flow sync, cron sync, retention). The Hangfire recurring dispatcher
  logs register / remove as EventIds 3100 / 3101. Update alert rules that matched old
  messages.

---

## Step 8 — Database

The SQL Server and PostgreSQL migrators still run automatically at startup. On a 1.34.x database both
only **add** the run-search indexes (and, on SQL Server, two computed columns) described below; 1.34.3 keeps working against the upgraded database, which is what makes the
[rollback](#rollback) below possible.

### Run-search columns and indexes (SQL Server / Azure SQL)

| Object | What | Cost |
|---|---|---|
| `FlowRuns.IdText` | computed `CONVERT(CHAR(36), Id)`, not persisted | metadata only, instant |
| `FlowRuns.RunSearchText` | computed, lower-cased, `Latin1_General_100_BIN2`: id, flow name, trigger key, status, job id | metadata only, instant |
| `IX_FlowRuns_IdText` | run-id prefix seek at any age | ~13 MB per 200k runs |
| `IX_FlowRuns_StartedAt_Search` | `(StartedAt DESC) INCLUDE (RunSearchText, FlowId, Status)` — quick search | ~43 MB per 200k runs |

The two columns are added synchronously at startup. The two indexes (measured 0.9 s together at 200k
runs) are built:

- **Azure SQL Database, Managed Instance, Enterprise / Developer:** online (`ONLINE = ON`) in the
  background after startup — no startup delay, writers are not blocked. Log events 7204 / 7205 mark
  start and end; 7206 a failure (retried on the next start).
- **Standard / Express / Web:** offline at startup while `FlowRuns` has at most 2,000,000 rows (seconds).
  Above that the migrator skips them and logs warning 7207 with the statements; run them in a maintenance
  window:

  ```sql
  CREATE INDEX [IX_FlowRuns_IdText] ON [FlowRuns]([IdText]);
  CREATE INDEX [IX_FlowRuns_StartedAt_Search] ON [FlowRuns]([StartedAt] DESC)
      INCLUDE ([RunSearchText], [FlowId], [Status]);
  ```

To avoid any startup work, create the two columns and indexes ahead of the deployment with the same
statements (`FlowOrchestratorSqlMigrator` skips whatever exists). Until the indexes exist, run search
uses query shapes that do not need them: correct, only slower.

Indexed computed columns require the standard `SET` options (`QUOTED_IDENTIFIER ON` and friends) on
every connection that inserts or updates `FlowRuns`. SqlClient and SSMS set them; run maintenance
scripts with `sqlcmd -I`.

**Rollback:** 1.34.x ignores the new columns and indexes. To remove them anyway:
`DROP INDEX [IX_FlowRuns_StartedAt_Search] ON [FlowRuns]; DROP INDEX [IX_FlowRuns_IdText] ON [FlowRuns];
ALTER TABLE [FlowRuns] DROP COLUMN [RunSearchText], [IdText];`

### Run search behaviour (SQL Server)

- Matching is still case-insensitive, but binary after lower-casing (BIN2): accent- and width-sensitive
  (`é` ≠ `e`, `ß` ≠ `ss`). Identifiers and ASCII text match as before.
- A hex-shaped term (4-35 hex digits / hyphens) also matches run ids that start with it at any age,
  ignoring the search window — the 8 characters the run list shows now find old runs.
- `/api/runs` caps its total at `FlowDashboardOptions.RunListTotalCountCap` (10,000) and returns
  `totalCapped: true` above it; set the option to `0` for an exact count.
- Custom `IFlowRunStore` implementations get `RunSearchQuery.After` / `TotalCountCap` through the default
  `SearchRunsAsync`; override it to page with a real keyset predicate.

### Run-search index (PostgreSQL)

| Object | What | Cost |
|---|---|---|
| `ix_flow_runs_run_search_trgm` | trigram GIN index on `lower(id::text \|\| '\|' \|\| flow_name \|\| … \|\| background_job_id)` — every quick search and the run-id prefix | ~47 MB per 200k runs |

No column is added and `flow_runs` is not rewritten: the index is an expression index, built with
`CREATE INDEX CONCURRENTLY` in the background after startup (log events 7211 / 7212; 7213 on failure,
retried on the next start). An index left `INVALID` by an interrupted build is dropped and rebuilt.
Until it exists, quick search runs the same SQL as a sequential scan.

Like the trigram indexes 1.x already created, it needs the `pg_trgm` extension. On **Azure Database for
PostgreSQL (Flexible Server)** allow-list it first (server parameter `azure.extensions` must include
`PG_TRGM`); otherwise the migrator logs a warning, skips every trigram index, and search stays correct but
scans. To build it ahead of the deployment:

```sql
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_flow_runs_run_search_trgm ON flow_runs USING gin ((
  lower(id::text || '|' || coalesce(flow_name, '') || '|' || coalesce(trigger_key, '') || '|' || status || '|' || coalesce(background_job_id, ''))
) gin_trgm_ops);
```

The expression must stay exactly as above: PostgreSQL only uses an expression index for a query that
repeats it.

Other PostgreSQL changes:

- Deep search probes the step rows with a correlated `EXISTS` (was `IN`): common words 33 s → 5 ms in the
  default window; rare terms unchanged.
- `ix_flow_steps_output_json_trgm` stays and is still created on new databases: no measurable step write
  cost, 200x faster deep search, but about half the size of `flow_steps`. A host that never uses the
  "search step output / errors" box can drop it.
- `ix_flow_runs_flow_name_trgm` and `ix_flow_runs_trigger_key_trgm` are no longer used by FlowOrchestrator's
  queries (the new index covers both columns). They are left in place; drop them if nothing of yours uses
  them: `DROP INDEX CONCURRENTLY IF EXISTS ix_flow_runs_flow_name_trgm;` (same for `trigger_key`).
- `UseRunSearchIndex()` is ignored on PostgreSQL (warning 7214).

**Rollback:** 1.34.x ignores the index; `DROP INDEX CONCURRENTLY IF EXISTS ix_flow_runs_run_search_trgm;`
removes it.

### Optional: the run-search token index (experimental, SQL Server)

`options.UseRunSearchIndex()` adds `FlowRunSearchTokens` and `FlowRunSearchIndexState` (created empty at
startup) and turns deep search into a token-index seek. Existing runs are back-filled newest-first by a
background service (log events 7301-7306); until it completes, deep search still scans the not-yet-indexed
part of the window with `LIKE`, so results stay complete. Budget roughly 72 token rows / ~6.6 KB per run
(1.3 GB and an 8-minute back-fill per 200k runs, measured), and one extra commit per step completion and
per run start (+5.4 ms p50 on the benchmark disk). Payload and error text then match by token prefix, not
substring. Enable it on **every** instance that writes runs; if you disable it later and enable it again,
reset the back-fill so runs written in between are indexed:

```sql
UPDATE FlowRunSearchIndexState SET BackfillAfterStartedAt = NULL, BackfillAfterId = NULL, BackfillCompletedAt = NULL;
```

To remove it: `DROP TABLE [FlowRunSearchTokens]; DROP TABLE [FlowRunSearchIndexState];`

### The run events table is no longer used

2.0 no longer writes or reads the run events table, and a **new** 2.0 database is created without it.
On a database that 1.34.x created, the table (and its index) is **left in place** — the migrator never
deletes data at startup — and retention stops touching it, so it keeps the 1.x history but does not grow
or shrink any more.

| Backend | Table | Index |
|---|---|---|
| SQL Server / Azure SQL | `FlowEvents` (the connection's default schema, normally `dbo`) | `IX_FlowEvents_RunId_Sequence` |
| PostgreSQL | `flow_events` (the connection's `search_path`, normally `public`) | `ix_flow_events_run_id_sequence` |

Under 2.0 the dashboard Events drawer derives every run's timeline — 1.x runs included — from the step
attempts, so the table only matters to a 1.34.x [rollback](#rollback), whose drawer reads it. Once you
are sure you will not roll back, reclaim the space by dropping it (the index goes with the table):

```sql
-- SQL Server / Azure SQL (run against the FlowOrchestrator database)
DROP TABLE IF EXISTS [FlowEvents];
```

```sql
-- PostgreSQL (run against the FlowOrchestrator database, same search_path as the app)
DROP TABLE IF EXISTS flow_events;
```

The table can be large (around 5.6 KB per run on SQL Server); on a busy database run the drop in a
maintenance window. Dropping it is safe while 2.0 is running.

More schema changes land before GA; see [Still landing before 2.0 GA](#still-landing-before-20-ga). From
that point this section will also list the migration the 2.0 migrator applies, how long it takes on
large tables, and how to run it ahead of the deployment.

---

## Step 9 — Verify after the upgrade

- [ ] All instances start; no `InvalidOperationException` from the [Step 5](#step-5--fix-what-the-new-startup-checks-report) checks.
- [ ] `/health` is green on every instance.
- [ ] The dashboard loads, shows **the same flows** as before, with the enabled flags and cron
      overrides you had (they live in the database, keyed by flow id).
- [ ] Trigger one run per important flow manually; it completes with the expected step statuses.
- [ ] A cron flow fires once per schedule — not twice (more than one runtime selected) and not zero
      (no runtime, or the flow is disabled).
- [ ] A webhook flow accepts a signed delivery; a replay is rejected if you enforce replay protection.
- [ ] A `WaitForSignal` flow resumes when signalled.
- [ ] A polling step re-polls and completes.
- [ ] Hangfire hosts: the Hangfire dashboard shows no failed jobs with `method not found` /
      `JobLoadException`; no 1.x instance is still consuming the queue.
- [ ] Non-Hangfire hosts: `dotnet list package --include-transitive` shows no Hangfire.
- [ ] Logs: no new error-level events (in particular 4103 / 4104 if you rely on the dashboard).

---

## Rollback

Because 2.0 only adds to an existing database (the run-search columns and indexes, which 1.34.3
ignores), rolling back to **1.34.3** is a redeploy:

1. Pause cron triggers and hold webhook traffic.
2. **Hangfire hosts:** wait until no 2.0 step job is Enqueued or Processing — a 1.x worker cannot run
   them. Scheduled 2.0 jobs left in the queue would fail on 1.x; either let them run first or re-trigger
   the affected runs after the rollback.
3. Stop the 2.0 instances, deploy 1.34.3, start, resume traffic.

Runs started on 2.0 that are still in progress continue on 1.34.3 (same tables), except for the Hangfire
jobs covered in step 2.

1.34.3 reads run events from `FlowEvents` again, which 2.0 did not write: runs executed under 2.0 show an
empty Events drawer on 1.34.3 (their steps, attempts and outputs are all there). If you already dropped
the table, 1.34.3's migrator recreates it empty at startup.

---

## Still landing before 2.0 GA

These are part of 2.0 but **not in the current alphas**. Plan for them now; this guide gains a step for
each when it lands:

- **Step data stored once.** Step outputs, step inputs and trigger data are no longer duplicated into
  `FlowOutputs`. This is a schema change with an automatic migration (and a backfill for runs that are
  in progress during the upgrade).
- **Store contract redesign.** `IFlowRunStore` / `IFlowRunRuntimeStore` / `IFlowRunControlStore` are
  consolidated into an engine store and a dashboard query store, without default interface methods, and
  with an incremental status read that removes the remaining per-step full-status fetch. Custom storage
  backends will need changes; the built-in ones carry them.
- **One input shape for untyped handlers.** Hangfire, InMemory and Service Bus will hand untyped
  handlers the same JSON-shaped values; typed handlers are unaffected.

---

## FAQ

**Do I have to move off Hangfire?**
No. Hangfire is a permanent, first-class runtime. 2.0 only stops forcing it on hosts that do not use it.

**Can I run 1.x and 2.0 side by side during the rollout?**
Against the same FlowOrchestrator database, yes, as long as they do not share a **Hangfire storage**
and only one side fires cron schedules
([Step 4](#step-4--move-the-hangfire-queue-across-hangfire-hosts-only)). With InMemory there is nothing
to share; with Service Bus, drain the step topic subscriptions before switching versions to be safe.

**We are on .NET 8 and cannot move yet.**
Stay on 1.34.x. The proposed support policy (roadmap #262, to be confirmed with 2.0 GA) is security and
critical fixes on 1.34.x for 6 months after 2.0 GA.

**Does 2.0 support multiple replicas?**
Yes, as 1.x did, with Hangfire or Service Bus on SQL Server / Azure SQL / PostgreSQL storage. InMemory
storage or runtime is single-process. Make sure you are on 1.34.3+ / 2.0 for the SQL Server concurrency
fixes.

**Where do I report a migration problem?**
Open an issue with your runtime, storage backend, target framework and the startup log:
<https://github.com/hoangsnowy/FlowOrchestrator/issues>.
