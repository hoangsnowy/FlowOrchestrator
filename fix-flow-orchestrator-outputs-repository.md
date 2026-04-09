# Fix: SqlOutputsRepository — persist step outputs to SQL Server

## Repo

https://github.com/hoangsnowy/FlowOrchestrator

## Bug

`IOutputsRepository` is always registered as `InMemoryOutputsRepository` (singleton), even when
`UseSqlServer()` is configured. Step outputs are lost on app restart, causing retries to fail with:

```
System.InvalidOperationException: Outputs from step 'validateAndStoreWebhookEvent' were not found for run '…'
```

Root cause in `FlowOrchestratorServiceCollectionExtensions.cs`:

```csharp
// This runs unconditionally — never replaced even when UseSqlServer() is called
services.AddSingleton<IOutputsRepository, InMemoryOutputsRepository>();

if (!string.IsNullOrEmpty(builder.Options.ConnectionString))
{
    services.AddFlowOrchestratorSqlServer(builder.Options.ConnectionString);
    // Only registers IFlowStore + IFlowRunStore — IOutputsRepository is NOT overridden
}
```

---

## IOutputsRepository interface (from `FlowOrchestrator.Core`)

```csharp
public interface IOutputsRepository
{
    ValueTask SaveStepOutputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result);
    ValueTask SaveTriggerDataAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger);
    ValueTask<object?> GetTriggerDataAsync(Guid runId);
    ValueTask SaveTriggerHeadersAsync(ITriggerContext ctx, IFlowDefinition flow, ITrigger trigger);
    ValueTask<IReadOnlyDictionary<string, string>?> GetTriggerHeadersAsync(Guid runId);
    ValueTask SaveStepInputAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);
    ValueTask<object?> GetStepOutputAsync(Guid runId, string stepKey);
    ValueTask EndScopeAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step);
    ValueTask RecordEventAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, FlowEvent evt);
}
```

---

## Storage design

New table: **`FlowOutputs`**

| Column | Type | Notes |
|---|---|---|
| `RunId` | `UNIQUEIDENTIFIER NOT NULL` | PK part 1 |
| `Key` | `NVARCHAR(256) NOT NULL` | PK part 2 |
| `ValueJson` | `NVARCHAR(MAX) NULL` | Serialized JSON |
| `CreatedAt` | `DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()` | |

Internal key conventions:

| Data | Key |
|---|---|
| Step output | `{stepKey}` |
| Step input | `{stepKey}:input` |
| Trigger data | `__trigger:data` |
| Trigger headers | `__trigger:headers` |

---

## Tasks

### 1. Create `SqlOutputsRepository.cs` in `FlowOrchestrator.SqlServer`

- Use **Dapper** + `Microsoft.Data.SqlClient` (already used by `SqlFlowRunStore`)
- Use `MERGE` (upsert) for all save operations
- `GetStepOutputAsync` → query by `(RunId, Key)`, deserialize to `JsonElement`
- `GetTriggerDataAsync` → key `__trigger:data`
- `GetTriggerHeadersAsync` → key `__trigger:headers`, deserialize to `IReadOnlyDictionary<string, string>`
- `EndScopeAsync` and `RecordEventAsync` → no-op (`ValueTask.CompletedTask`)

### 2. Add migration SQL to `FlowOrchestratorSqlMigrator.cs`

Add to the existing `MigrationSql` string (idempotent):

```sql
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowOutputs')
BEGIN
    CREATE TABLE [FlowOutputs] (
        [RunId]      UNIQUEIDENTIFIER NOT NULL,
        [Key]        NVARCHAR(256)    NOT NULL,
        [ValueJson]  NVARCHAR(MAX)    NULL,
        [CreatedAt]  DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT PK_FlowOutputs PRIMARY KEY ([RunId], [Key])
    );
END
```

### 3. Register in `SqlServerServiceCollectionExtensions.cs`

```csharp
public static IServiceCollection AddFlowOrchestratorSqlServer(
    this IServiceCollection services, string connectionString)
{
    services.AddSingleton<IFlowStore>(_ => new SqlFlowStore(connectionString));
    services.AddSingleton<IFlowRunStore>(_ => new SqlFlowRunStore(connectionString));
    services.AddSingleton<IOutputsRepository>(_ => new SqlOutputsRepository(connectionString)); // ADD THIS
    services.AddHostedService(sp => new FlowOrchestratorSqlMigrator(...));
    return services;
}
```

### 4. Fix `FlowOrchestratorServiceCollectionExtensions.cs`

Move `InMemoryOutputsRepository` into the `else` branch so it is only registered when no SQL connection string is provided:

```csharp
// BEFORE (buggy):
services.AddSingleton<IOutputsRepository, InMemoryOutputsRepository>(); // unconditional

if (!string.IsNullOrEmpty(builder.Options.ConnectionString))
    services.AddFlowOrchestratorSqlServer(builder.Options.ConnectionString);
else
{
    services.AddSingleton<IFlowStore, InMemoryFlowStore>();
    services.AddSingleton<IFlowRunStore, InMemoryFlowRunStore>();
}

// AFTER (fixed):
if (!string.IsNullOrEmpty(builder.Options.ConnectionString))
    services.AddFlowOrchestratorSqlServer(builder.Options.ConnectionString); // registers SqlOutputsRepository
else
{
    services.AddSingleton<IFlowStore, InMemoryFlowStore>();
    services.AddSingleton<IFlowRunStore, InMemoryFlowRunStore>();
    services.AddSingleton<IOutputsRepository, InMemoryOutputsRepository>(); // only in-memory mode
}
```

---

## Acceptance criteria

- [ ] App restart mid-flow: retry from a failed step succeeds (outputs from previous steps are read from SQL)
- [ ] In-memory mode (no connection string): `InMemoryOutputsRepository` is still used — no regression
- [ ] New `FlowOutputs` table is created automatically on startup via existing migrator
