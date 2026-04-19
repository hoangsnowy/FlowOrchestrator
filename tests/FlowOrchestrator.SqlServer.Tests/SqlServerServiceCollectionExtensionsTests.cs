using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SqlServer.Tests;

public sealed class SqlServerServiceCollectionExtensionsTests
{
    [Fact]
    public void AddFlowOrchestratorSqlServer_registers_IFlowStore_as_SqlFlowStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestratorSqlServer("Server=.;Database=test;");

        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IFlowStore>().Should().BeOfType<SqlFlowStore>();
    }

    [Fact]
    public void AddFlowOrchestratorSqlServer_registers_IFlowRunStore_as_SqlFlowRunStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestratorSqlServer("Server=.;Database=test;");

        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IFlowRunStore>().Should().BeOfType<SqlFlowRunStore>();
    }

    [Fact]
    public void AddFlowOrchestratorSqlServer_registers_IOutputsRepository_as_SqlOutputsRepository()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestratorSqlServer("Server=.;Database=test;");

        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IOutputsRepository>().Should().BeOfType<SqlOutputsRepository>();
    }

    [Fact]
    public void AddFlowOrchestratorSqlServer_registers_migrator_as_IHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestratorSqlServer("Server=.;Database=test;");

        var sp = services.BuildServiceProvider();

        var hostedServices = sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
        hostedServices.Should().Contain(s => s is FlowOrchestratorSqlMigrator);
    }
}
