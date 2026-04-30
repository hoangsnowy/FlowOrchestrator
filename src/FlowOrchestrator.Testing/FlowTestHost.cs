using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Testing;

/// <summary>
/// Static entry point for building an in-process test host around a flow definition.
/// Returns a fluent <see cref="FlowTestHostBuilder{TFlow}"/> that configures step handlers,
/// fake services, polling overrides, and clock substitutions before <c>BuildAsync()</c> spins up
/// an isolated <c>IHost</c> backed by the in-memory storage and runtime.
/// </summary>
/// <example>
/// <code>
/// await using var host = await FlowTestHost.For&lt;OrderFulfillmentFlow&gt;()
///     .WithHandler&lt;FetchOrdersHandler&gt;("FetchOrders")
///     .WithService&lt;IOrderRepository&gt;(new FakeOrderRepository())
///     .BuildAsync();
///
/// var result = await host.TriggerAsync(body: new { orderId = "ord_123" });
///
/// Assert.Equal(RunStatus.Succeeded, result.Status);
/// </code>
/// </example>
public static class FlowTestHost
{
    /// <summary>
    /// Begins building a test host for the given flow type.
    /// </summary>
    /// <typeparam name="TFlow">The flow under test. Must have a parameterless constructor.</typeparam>
    public static FlowTestHostBuilder<TFlow> For<TFlow>() where TFlow : class, IFlowDefinition, new() =>
        new();
}
