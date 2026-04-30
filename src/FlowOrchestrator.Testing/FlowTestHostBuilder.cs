using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.Testing.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Testing;

/// <summary>
/// Fluent builder returned by <see cref="FlowTestHost.For{TFlow}"/>.
/// Each <c>With*</c> call mutates and returns the same builder; <see cref="BuildAsync"/>
/// finalises configuration, starts the in-process host, and returns a <see cref="FlowTestHost{TFlow}"/>
/// ready to receive triggers.
/// </summary>
/// <typeparam name="TFlow">The flow under test.</typeparam>
public sealed class FlowTestHostBuilder<TFlow> where TFlow : class, IFlowDefinition, new()
{
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = new();
    private readonly List<Action<FlowOrchestratorBuilder>> _orchestratorConfigurations = new();
    private Action<ILoggingBuilder>? _loggingConfiguration;
    private FrozenTimeProvider? _frozenTimeProvider;
    private TimeSpan? _fastPollingMaxDelay;

    internal FlowTestHostBuilder() { }

    /// <summary>
    /// Registers <typeparamref name="THandler"/> as the handler for steps whose <c>type</c> equals <paramref name="stepType"/>.
    /// </summary>
    /// <typeparam name="THandler">Concrete handler class (constructor-injected via the host's DI).</typeparam>
    /// <param name="stepType">Manifest step type label that selects this handler.</param>
    public FlowTestHostBuilder<TFlow> WithHandler<THandler>(string stepType) where THandler : class
    {
        _serviceConfigurations.Add(services => services.AddStepHandler<THandler>(stepType));
        return this;
    }

    /// <summary>
    /// Registers <paramref name="instance"/> as the resolved value for <typeparamref name="TService"/>.
    /// </summary>
    /// <typeparam name="TService">The service contract to satisfy.</typeparam>
    /// <param name="instance">A fake or real implementation supplied by the test.</param>
    public FlowTestHostBuilder<TFlow> WithService<TService>(TService instance) where TService : class
    {
        _serviceConfigurations.Add(services => services.AddSingleton<TService>(instance));
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TImpl"/> as the singleton implementation for <typeparamref name="TService"/>,
    /// resolving its constructor dependencies from the host's DI container.
    /// </summary>
    public FlowTestHostBuilder<TFlow> WithService<TService, TImpl>()
        where TImpl : class, TService
        where TService : class
    {
        _serviceConfigurations.Add(services => services.AddSingleton<TService, TImpl>());
        return this;
    }

    /// <summary>Configures the logging pipeline used by the host (defaults: silent).</summary>
    public FlowTestHostBuilder<TFlow> WithLogging(Action<ILoggingBuilder> configure)
    {
        _loggingConfiguration = configure;
        return this;
    }

    /// <summary>
    /// Freezes the clock used by the in-memory cron dispatcher to <paramref name="now"/>.
    /// Call <see cref="FlowTestHost{TFlow}.FastForwardAsync"/> to advance the frozen clock.
    /// </summary>
    /// <remarks>
    /// The cron dispatcher's internal <c>PeriodicTimer(1s)</c> still ticks in real time;
    /// tests must wait at least one real second after a fast-forward for the next tick to read the new clock value.
    /// </remarks>
    public FlowTestHostBuilder<TFlow> WithSystemClock(DateTimeOffset now)
    {
        _frozenTimeProvider = new FrozenTimeProvider(now);
        return this;
    }

    /// <summary>
    /// Clamps the delay parameter passed to <c>IStepDispatcher.ScheduleStepAsync</c>
    /// so polling steps re-execute almost immediately instead of waiting their manifest-declared interval.
    /// Default cap: 100ms.
    /// </summary>
    /// <param name="maxDelay">Maximum delay allowed; <see langword="null"/> selects the default 100ms.</param>
    public FlowTestHostBuilder<TFlow> WithFastPolling(TimeSpan? maxDelay = null)
    {
        _fastPollingMaxDelay = maxDelay ?? TimeSpan.FromMilliseconds(100);
        return this;
    }

    /// <summary>
    /// Escape hatch for advanced configuration on the underlying <see cref="FlowOrchestratorBuilder"/>
    /// (e.g. enabling event persistence, tweaking retention, replacing services beyond <c>WithService</c>).
    /// </summary>
    public FlowTestHostBuilder<TFlow> WithCustomConfiguration(Action<FlowOrchestratorBuilder> configure)
    {
        _orchestratorConfigurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Constructs the in-process host, starts hosted services, and returns a <see cref="FlowTestHost{TFlow}"/>
    /// ready to receive triggers.
    /// </summary>
    public async Task<FlowTestHost<TFlow>> BuildAsync()
    {
        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                _loggingConfiguration?.Invoke(logging);
            })
            .ConfigureServices(services =>
            {
                if (_frozenTimeProvider is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_frozenTimeProvider));
                }

                services.AddFlowOrchestrator(opts =>
                {
                    opts.UseInMemory();
                    opts.UseInMemoryRuntime();
                    opts.AddFlow<TFlow>();
                    opts.Observability.EnableEventPersistence = true;

                    foreach (var configure in _orchestratorConfigurations)
                    {
                        configure(opts);
                    }
                });

                foreach (var configure in _serviceConfigurations)
                {
                    configure(services);
                }

                if (_fastPollingMaxDelay is { } cap)
                {
                    var existing = services.LastOrDefault(d => d.ServiceType == typeof(IStepDispatcher))
                        ?? throw new InvalidOperationException(
                            "WithFastPolling: no IStepDispatcher registered. Did UseInMemoryRuntime() get suppressed?");
                    services.Remove(existing);
                    services.AddSingleton<IStepDispatcher>(sp =>
                    {
                        IStepDispatcher inner;
                        if (existing.ImplementationFactory is { } factory)
                        {
                            inner = (IStepDispatcher)factory(sp);
                        }
                        else if (existing.ImplementationInstance is IStepDispatcher instance)
                        {
                            inner = instance;
                        }
                        else if (existing.ImplementationType is { } implType)
                        {
                            inner = (IStepDispatcher)ActivatorUtilities.CreateInstance(sp, implType);
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                "WithFastPolling: existing IStepDispatcher descriptor has no implementation to wrap.");
                        }
                        return new FastPollingStepDispatcher(inner, cap);
                    });

                    // The v2 runtime claim guard never releases after a Pending result, so polling
                    // reschedules cannot re-dispatch the same step. Single-worker test runs do not need
                    // claim exclusion, so swap in a permissive wrapper that always grants the claim.
                    var runtimeStoreDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IFlowRunRuntimeStore))
                        ?? throw new InvalidOperationException(
                            "WithFastPolling: no IFlowRunRuntimeStore registered. Did UseInMemory() get suppressed?");
                    services.Remove(runtimeStoreDescriptor);
                    services.AddSingleton<IFlowRunRuntimeStore>(sp =>
                    {
                        IFlowRunRuntimeStore inner;
                        if (runtimeStoreDescriptor.ImplementationFactory is { } factory)
                        {
                            inner = (IFlowRunRuntimeStore)factory(sp);
                        }
                        else if (runtimeStoreDescriptor.ImplementationInstance is IFlowRunRuntimeStore instance)
                        {
                            inner = instance;
                        }
                        else if (runtimeStoreDescriptor.ImplementationType is { } implType)
                        {
                            inner = (IFlowRunRuntimeStore)ActivatorUtilities.CreateInstance(sp, implType);
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                "WithFastPolling: existing IFlowRunRuntimeStore descriptor has no implementation to wrap.");
                        }
                        return new PermissiveRuntimeStore(inner);
                    });
                }
            });

        var host = hostBuilder.Build();
        await host.StartAsync().ConfigureAwait(false);
        return new FlowTestHost<TFlow>(host, _frozenTimeProvider);
    }
}
