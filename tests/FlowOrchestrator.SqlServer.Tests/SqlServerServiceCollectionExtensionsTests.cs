using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SqlServer.Tests;

public sealed class SqlServerServiceCollectionExtensionsTests
{
    [Fact]
    public void AddFlowOrchestratorSqlServer_registers_IFlowStore_as_SqlFlowStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestratorSqlServer("Server=.;Database=test;");

        // Act
        var sp = services.BuildServiceProvider();

        // Assert
        Assert.IsType<SqlFlowStore>(sp.GetRequiredService<IFlowStore>());
    }

    [Fact]
    public void AddFlowOrchestratorSqlServer_registers_IFlowRunStore_as_SqlFlowRunStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestratorSqlServer("Server=.;Database=test;");

        // Act
        var sp = services.BuildServiceProvider();

        // Assert
        Assert.IsType<SqlFlowRunStore>(sp.GetRequiredService<IFlowRunStore>());
    }

    [Fact]
    public void AddFlowOrchestratorSqlServer_registers_IOutputsRepository_as_SqlOutputsRepository()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestratorSqlServer("Server=.;Database=test;");

        // Act
        var sp = services.BuildServiceProvider();

        // Assert
        Assert.IsType<SqlOutputsRepository>(sp.GetRequiredService<IOutputsRepository>());
    }

    [Fact]
    public void AddFlowOrchestratorSqlServer_registers_migrator_as_IHostedService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestratorSqlServer("Server=.;Database=test;");

        // Act
        var sp = services.BuildServiceProvider();

        // Assert
        var hostedServices = sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
        Assert.Contains(hostedServices, s => s is FlowOrchestratorSqlMigrator);
    }
}
