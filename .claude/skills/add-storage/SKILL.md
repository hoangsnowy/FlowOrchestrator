Implement a custom storage backend for FlowOrchestrator by scaffolding the three required repository interfaces, a DI extension method, and test stubs.

## What to gather first

Before writing any code, ask the user (or infer from context):
- **Backend name** — e.g. `Redis`, `MongoDB`, `PostgreSQL`, `InMemory`
- **Target project location** — new project under `src/FlowOrchestrator.{Backend}/` or add to an existing project
- **Connection/config** — what configuration the backend needs (connection string, endpoint URL, etc.)

## The three interfaces to implement

All three live in `FlowOrchestrator.Core`:

| Interface | File | Responsibility |
|---|---|---|
| `IFlowStore` | `src/FlowOrchestrator.Core/Storage/IFlowStore.cs` | CRUD for `FlowDefinitionRecord` (flow catalog) |
| `IFlowRunStore` | `src/FlowOrchestrator.Core/Storage/IFlowRunStore.cs` | Run lifecycle: start, record step, complete, list, retry |
| `IOutputsRepository` | `src/FlowOrchestrator.Core/Storage/IOutputsRepository.cs` | Trigger data, headers, step inputs/outputs, events |

Read all three interface files before writing implementations.

## Implementation steps (execute without stopping)

### 1. Create the project (if new)

```
src/
  FlowOrchestrator.{Backend}/
    FlowOrchestrator.{Backend}.csproj
    {Backend}FlowStore.cs
    {Backend}FlowRunStore.cs
    {Backend}OutputsRepository.cs
    ServiceCollectionExtensions.cs   ← DI extension
```

The `.csproj` should reference `FlowOrchestrator.Core` and target `net8.0;net9.0;net10.0` (match `Directory.Build.props`).

### 2. Implement each interface

Use the SQL Server implementations in `src/FlowOrchestrator.SqlServer/` as the reference. Read:
- `SqlFlowStore.cs`
- `SqlFlowRunStore.cs`
- `SqlOutputsRepository.cs`

Key patterns to follow:
- Use `ValueTask` for all async methods (not `Task`) — the existing interface contracts require this
- JSON serialization uses `System.Text.Json` throughout — stay consistent
- `FlowOutputs` are keyed by `(RunId, key)` — the key for trigger data is `"__trigger"`, for headers `"__trigger_headers"`, for step input `"{stepKey}:input"`, for step output `"{stepKey}:output"`
- `DashboardStatistics` is returned by `IFlowRunStore.GetStatisticsAsync()` — include run counts by status

### 3. Add the DI extension

File: `src/FlowOrchestrator.{Backend}/ServiceCollectionExtensions.cs`

```csharp
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.{Backend};

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers {Backend} implementations of IFlowStore, IFlowRunStore, and IOutputsRepository.
    /// Call this inside AddFlowOrchestrator(options => options.Use{Backend}(...)).
    /// </summary>
    public static FlowOrchestratorBuilder Use{Backend}(
        this FlowOrchestratorBuilder builder,
        string connectionString /* or whatever config the backend needs */)
    {
        builder.Services.AddSingleton<IFlowStore, {Backend}FlowStore>(
            sp => new {Backend}FlowStore(connectionString));
        builder.Services.AddSingleton<IFlowRunStore, {Backend}FlowRunStore>(
            sp => new {Backend}FlowRunStore(connectionString));
        builder.Services.AddSingleton<IOutputsRepository, {Backend}OutputsRepository>(
            sp => new {Backend}OutputsRepository(connectionString));
        return builder;
    }
}
```

Usage in the host app:
```csharp
builder.Services.AddFlowOrchestrator(options =>
{
    options.Use{Backend}(connectionString);
    options.UseHangfire();
    options.AddFlow<MyFlow>();
});
```

### 4. Write unit tests

File: `tests/FlowOrchestrator.{Backend}.Tests/`

Tests to write:
- `FlowStore`: SaveAsync → GetByIdAsync round-trip; SetEnabledAsync toggles IsEnabled; DeleteAsync removes record
- `FlowRunStore`: StartRunAsync → RecordStepStartAsync → RecordStepCompleteAsync → CompleteRunAsync state machine
- `OutputsRepository`: SaveTriggerDataAsync → GetTriggerDataAsync round-trip; SaveStepOutputAsync → GetStepOutputAsync
- Edge cases: GetByIdAsync with unknown ID returns null; GetRunsPageAsync pagination respects skip/take

Use xUnit + FluentAssertions. For backends that require external services (Redis, MongoDB), use integration test fixtures or embedded/testcontainer equivalents.

### 5. Register the auto-migrator (if applicable)

If the backend needs schema/index creation on startup, implement `IHostedService` similar to `FlowOrchestratorSqlMigrator` in `src/FlowOrchestrator.SqlServer/`:

```csharp
public sealed class {Backend}FlowOrchestratorMigrator : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        // Create collections/tables/indexes here
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

Register it alongside the repository registrations in `Use{Backend}(...)`.

### 6. Build and test

```bash
dotnet build
dotnet test
```

Fix all errors. Report: files created, interfaces implemented, build status, test count.

## Reference implementations

| File | What it shows |
|---|---|
| `src/FlowOrchestrator.SqlServer/SqlFlowStore.cs` | Full IFlowStore over Dapper/SQL |
| `src/FlowOrchestrator.SqlServer/SqlFlowRunStore.cs` | Full IFlowRunStore with pagination + stats |
| `src/FlowOrchestrator.SqlServer/SqlOutputsRepository.cs` | Full IOutputsRepository with trigger + step storage |
| `src/FlowOrchestrator.Core/Storage/InMemoryFlowStore.cs` | Simple in-memory IFlowStore (good starting point) |
| `src/FlowOrchestrator.SqlServer/FlowOrchestratorSqlMigrator.cs` | Schema migration pattern |
