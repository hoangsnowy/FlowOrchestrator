global using Xunit;

// One SQL Server and one PostgreSQL container for the whole assembly; every test creates its own databases
// in them. Tests run one at a time: the SQL Server container is shared with nothing else, and the seeding
// hosts run real engines whose background services should not compete for the same containers.
[assembly: AssemblyFixture(typeof(FlowOrchestrator.StorageMigration.IntegrationTests.DatabaseFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
