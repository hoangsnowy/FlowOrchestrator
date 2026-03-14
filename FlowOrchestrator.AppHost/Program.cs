using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// SQL Server resource (container + database)
// A fixed password avoids "Login failed for 'sa'" when a persisted data volume
// already contains master-db credentials from a previous run with a different
// randomly-generated password.
var sqlPassword = builder.AddParameter("sql-password", secret: true);
var sql = builder.AddSqlServer("sql", password: sqlPassword)
                 .WithDataVolume()
                 .AddDatabase("FlowOrchestrator");

// Web app project (existing SampleApp) wired to SQL – wait for SQL to be healthy first
builder.AddProject<Projects.FlowOrchestrator_SampleApp>("flow-orchestrator-web")
       .WithReference(sql)
       .WaitFor(sql);

builder.Build().Run();
