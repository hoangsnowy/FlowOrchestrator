using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// xUnit fixture that boots the official Microsoft Azure Service Bus emulator and its required
/// SQL Edge sidecar on a shared Docker network, mounts a generated <c>Config.json</c>, and exposes
/// a usable connection string. Reused across the whole assembly via xUnit's collection mechanism so
/// the containers are started once.
/// </summary>
/// <remarks>
/// The emulator does NOT support administrative entity creation: every queue, topic, and
/// subscription must be declared in the JSON config mounted at startup. We pre-declare:
/// <list type="bullet">
///   <item>Topic <c>flow-steps</c></item>
///   <item>Queue <c>flow-cron-triggers</c></item>
///   <item>Subscription <c>flow-{<see cref="TestFlowId"/>}</c> with a SQL filter on <c>FlowId</c></item>
/// </list>
/// Tests must use <see cref="TestFlowId"/> as the flow id. Tests requiring more flows must be
/// rebuilt against a different fixture or extend this config.
/// </remarks>
public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    /// <summary>The single flow id whose subscription is pre-provisioned.</summary>
    public static readonly Guid TestFlowId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    /// <summary>The flow id whose subscription is pre-provisioned for cron round-trip tests.</summary>
    public static readonly Guid CronTestFlowId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private const string SqlPassword = "Local-Dev-Pwd-2026!";

    private INetwork? _network;
    private IContainer? _sqlEdge;
    private IContainer? _emulator;
    private string? _configPath;

    /// <summary>Connection string the test code uses to talk to the emulator.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _sqlEdge = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/azure-sql-edge:latest")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", SqlPassword)
            .WithNetwork(_network)
            .WithNetworkAliases("sb-sqledge")
            .WithPortBinding(1433, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Recovery is complete"))
            .Build();

        await _sqlEdge.StartAsync();

        _configPath = Path.Combine(Path.GetTempPath(), $"sb-emulator-config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(_configPath, BuildConfigJson(TestFlowId, CronTestFlowId));

        var freeAmqp = GetFreeTcpPort();

        _emulator = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("SQL_SERVER", "sb-sqledge")
            .WithEnvironment("MSSQL_SA_PASSWORD", SqlPassword)
            .WithNetwork(_network)
            .WithBindMount(_configPath, "/ServiceBus_Emulator/ConfigFiles/Config.json", AccessMode.ReadOnly)
            .WithPortBinding(freeAmqp, 5672)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Emulator Service is Successfully Up!"))
            .Build();

        await _emulator.StartAsync();

        ConnectionString =
            $"Endpoint=sb://localhost:{freeAmqp};SharedAccessKeyName=RootManageSharedAccessKey;" +
            "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_emulator is not null) await _emulator.DisposeAsync();
        if (_sqlEdge is not null) await _sqlEdge.DisposeAsync();
        if (_network is not null) await _network.DisposeAsync();
        if (_configPath is not null && File.Exists(_configPath))
        {
            try { File.Delete(_configPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static string BuildConfigJson(Guid manualFlowId, Guid cronFlowId)
    {
        var manualSub = $"flow-{manualFlowId}";
        var cronSub = $"flow-{cronFlowId}";
        return $@"{{
  ""UserConfig"": {{
    ""Namespaces"": [
      {{
        ""Name"": ""sbemulator"",
        ""Queues"": [
          {{ ""Name"": ""flow-cron-triggers"", ""Properties"": {{ ""MaxDeliveryCount"": 10, ""RequiresDuplicateDetection"": false, ""RequiresSession"": false }} }}
        ],
        ""Topics"": [
          {{
            ""Name"": ""flow-steps"",
            ""Properties"": {{ ""RequiresDuplicateDetection"": false }},
            ""Subscriptions"": [
              {{
                ""Name"": ""{manualSub}"",
                ""Properties"": {{ ""MaxDeliveryCount"": 10 }},
                ""Rules"": [
                  {{ ""Name"": ""flow-filter"", ""Properties"": {{ ""FilterType"": ""Sql"", ""SqlFilter"": {{ ""SqlExpression"": ""FlowId = '{manualFlowId}'"" }} }} }}
                ]
              }},
              {{
                ""Name"": ""{cronSub}"",
                ""Properties"": {{ ""MaxDeliveryCount"": 10 }},
                ""Rules"": [
                  {{ ""Name"": ""flow-filter"", ""Properties"": {{ ""FilterType"": ""Sql"", ""SqlFilter"": {{ ""SqlExpression"": ""FlowId = '{cronFlowId}'"" }} }} }}
                ]
              }}
            ]
          }}
        ]
      }}
    ],
    ""Logging"": {{ ""Type"": ""Console"" }}
  }}
}}";
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>xUnit collection name binding the emulator fixture to all tests in this assembly.</summary>
[CollectionDefinition(Name)]
public sealed class ServiceBusEmulatorCollection : ICollectionFixture<ServiceBusEmulatorFixture>
{
    /// <summary>The collection name used by <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "ServiceBusEmulator";
}
