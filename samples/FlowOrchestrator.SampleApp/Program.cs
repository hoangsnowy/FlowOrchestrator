using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Dashboard;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.SampleApp;
using FlowOrchestrator.SampleApp.Flows;
using FlowOrchestrator.SampleApp.Steps;
using Hangfire;
using Hangfire.SqlServer;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("FlowOrchestrator")
    ?? throw new InvalidOperationException(
        "Connection string 'FlowOrchestrator' not configured. " +
        "When running via Aspire, this is injected automatically. " +
        "For local development, add it to appsettings.Development.json under ConnectionStrings:FlowOrchestrator.");

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions()));

builder.Services.AddHangfireServer();

builder.Services.AddFlowOrchestrator(options =>
{
    options.UseSqlServer(connectionString);
    options.UseHangfire();
    options.AddFlow<HelloWorldFlow>();
    options.AddFlow<OrderProcessingFlow>();
    options.AddFlow<PollingDemoFlow>();
    options.AddFlow<WebhookTriggerBodyTestFlow>();
});

builder.Services.AddStepHandler<LogMessageStepHandler>("LogMessage");
builder.Services.AddStepHandler<QueryDatabaseStep>("QueryDatabase");
builder.Services.AddStepHandler<CallExternalApiStep>("CallExternalApi");
builder.Services.AddStepHandler<SaveResultStep>("SaveResult");
builder.Services.AddStepHandler<SerializeProbeStep>("SerializeProbe");

builder.Services.AddSingleton(new DbConnectionFactory(connectionString));
builder.Services.AddHostedService(sp =>
    new SampleDataMigrator(connectionString, sp.GetRequiredService<ILogger<SampleDataMigrator>>()));
builder.Services.AddHttpClient("ExternalApi", client =>
{
    client.BaseAddress = new Uri("https://jsonplaceholder.typicode.com");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddFlowDashboard(builder.Configuration);

var app = builder.Build();

app.UseStaticFiles();
app.UseHangfireDashboard("/hangfire");
app.MapFlowDashboard("/flows");

app.MapGet("/", () => "FlowOrchestrator.SampleApp is running. Visit /flows for the dashboard.");

app.Run();
