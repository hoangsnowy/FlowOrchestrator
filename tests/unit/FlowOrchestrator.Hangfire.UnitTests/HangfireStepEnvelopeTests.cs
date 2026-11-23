using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using NSubstitute;

namespace FlowOrchestrator.Hangfire.Tests;

/// <summary>
/// The 2.0 Hangfire step job carries one <see cref="HangfireStepEnvelope"/>. These tests push a step
/// through the real dispatcher and Hangfire's own job (de)serialiser (<see cref="InvocationData"/>, the
/// path every Hangfire storage uses) and run it through <see cref="HangfireFlowOrchestrator"/>.
/// </summary>
public sealed class HangfireStepEnvelopeTests
{
    private const string TriggerSentinel = "trigger-only-sentinel-7c1e";

    private readonly IBackgroundJobClient _client = Substitute.For<IBackgroundJobClient>();
    private readonly IFlowOrchestrator _engine = Substitute.For<IFlowOrchestrator>();
    private readonly IFlowDefinition _flow = Substitute.For<IFlowDefinition>();
    private readonly List<Job> _jobs = new();

    public HangfireStepEnvelopeTests()
    {
        _flow.Id.Returns(Guid.NewGuid());
        _client.Create(Arg.Do<Job>(_jobs.Add), Arg.Any<IState>()).Returns("job-1");
        _engine.RunStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>((object?)null));
    }

    private HangfireFlowOrchestrator CreateRunner() => new(_engine, new FlowRegistry([_flow]));

    private (IExecutionContext Context, IFlowDefinition Flow, IStepInstance Step) CapturedEngineCall()
    {
        var call = Assert.Single(_engine.ReceivedCalls());
        var args = call.GetArguments();
        return ((IExecutionContext)args[0]!, (IFlowDefinition)args[1]!, (IStepInstance)args[2]!);
    }

    /// <summary>Round-trips the captured job exactly as Hangfire storage would, and returns its envelope.</summary>
    private HangfireStepEnvelope RoundTripCapturedJob(out string serializedArguments)
    {
        var job = Assert.Single(_jobs);
        var invocation = InvocationData.SerializeJob(job);
        serializedArguments = invocation.Arguments;
        var restored = invocation.DeserializeJob();
        return Assert.IsType<HangfireStepEnvelope>(restored.Args[0]);
    }

    [Fact]
    public async Task ForEachItemAndTriggerValue_WithDollarTypeKeys_RoundTripAsPlainData()
    {
        // Arrange — "$type" is Newtonsoft's type-metadata key; under TypeNameHandling it would select
        // a CLR type. Here it sits inside a ForEach item and inside a trigger-derived JSON value.
        var dispatcher = new HangfireStepDispatcher(_client);
        var ctx = new Core.Execution.ExecutionContext
        {
            RunId = Guid.NewGuid(),
            PrincipalId = "user-1",
            TriggerData = JsonDocument.Parse($$"""{"$type":"System.IO.FileInfo, System.IO.FileSystem","marker":"{{TriggerSentinel}}"}""").RootElement.Clone(),
            TriggerHeaders = new Dictionary<string, string> { ["X-Marker"] = TriggerSentinel },
        };
        var scheduledTime = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var step = new StepInstance("loop.3.child", "Process")
        {
            RunId = ctx.RunId,
            ScheduledTime = scheduledTime,
            Index = 3,
            Inputs = new Dictionary<string, object?>
            {
                ["__loopItem"] = new Dictionary<string, object?>
                {
                    ["$type"] = "System.Diagnostics.Process, System.Diagnostics.Process",
                    ["sku"] = "A-1",
                    ["qty"] = 2L,
                },
                ["fromTrigger"] = JsonDocument.Parse("""{"$type":"System.Object","nested":{"$type":"x"},"list":[1,2.5,true,null]}""").RootElement.Clone(),
                ["orderId"] = 42L,
                ["note"] = null,
            },
        };

        // Act
        await dispatcher.EnqueueStepAsync(ctx, _flow, step, CancellationToken.None);
        var envelope = RoundTripCapturedJob(out var serializedArguments);
        await CreateRunner().RunStepAsync(envelope);

        // Assert — identity and scheduling data survive.
        var (runCtx, runFlow, runStep) = CapturedEngineCall();
        Assert.Same(_flow, runFlow);
        Assert.Equal(ctx.RunId, runCtx.RunId);
        Assert.Equal("user-1", runCtx.PrincipalId);
        Assert.Equal("loop.3.child", runStep.Key);
        Assert.Equal("Process", runStep.Type);
        Assert.Equal(3, runStep.Index);
        Assert.Equal(scheduledTime, runStep.ScheduledTime);

        // "$type" keys are ordinary dictionary entries, not type metadata.
        var item = Assert.IsType<Dictionary<string, object?>>(runStep.Inputs["__loopItem"]);
        Assert.Equal("System.Diagnostics.Process, System.Diagnostics.Process", item["$type"]);
        Assert.Equal("A-1", item["sku"]);
        Assert.Equal(2L, item["qty"]);

        var fromTrigger = Assert.IsType<Dictionary<string, object?>>(runStep.Inputs["fromTrigger"]);
        Assert.Equal("System.Object", fromTrigger["$type"]);
        var nested = Assert.IsType<Dictionary<string, object?>>(fromTrigger["nested"]);
        Assert.Equal("x", nested["$type"]);
        Assert.Equal(new object?[] { 1L, 2.5d, true, null }, Assert.IsType<List<object?>>(fromTrigger["list"]));

        Assert.Equal(42L, runStep.Inputs["orderId"]);
        Assert.True(runStep.Inputs.TryGetValue("note", out var note));
        Assert.Null(note);

        // Trigger body and headers never enter the job payload — the engine reloads them from the store.
        Assert.DoesNotContain(TriggerSentinel, serializedArguments, StringComparison.Ordinal);
        Assert.Null(runCtx.TriggerData);
        Assert.Null(runCtx.TriggerHeaders);
    }

    [Fact]
    public async Task EnqueueAndSchedule_BothTargetTheEnvelopeOverload()
    {
        // Arrange
        var dispatcher = new HangfireStepDispatcher(_client);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("poll", "Poll") { RunId = ctx.RunId };

        // Act
        await dispatcher.EnqueueStepAsync(ctx, _flow, step, CancellationToken.None);
        await dispatcher.ScheduleStepAsync(ctx, _flow, step, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Assert — a single, concrete DTO argument (plus Hangfire's injected PerformContext).
        Assert.Equal(2, _jobs.Count);
        Assert.All(_jobs, job =>
        {
            Assert.Equal(typeof(IHangfireStepRunner), job.Type);
            var parameters = job.Method.GetParameters();
            Assert.Equal(typeof(HangfireStepEnvelope), parameters[0].ParameterType);
            Assert.Equal(2, parameters.Length);
            Assert.False(job.Method.IsDefined(typeof(ObsoleteAttribute), inherit: false));
        });
    }

    [Fact]
    public void From_StepWithoutInputs_CarriesNoInputsJson_AndRebuildsAnEmptyInputMap()
    {
        // Arrange
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("a", "A") { RunId = ctx.RunId };

        // Act
        var envelope = HangfireStepEnvelope.From(ctx, _flow.Id, step);
        var rebuilt = envelope.ToStepInstance();

        // Assert
        Assert.Null(envelope.InputsJson);
        Assert.Empty(rebuilt.Inputs);
        Assert.Equal(ctx.RunId, rebuilt.RunId);
    }

    [Fact]
    public void ToStepInstance_InputsThatAreNotAJsonObject_Throws()
    {
        // Arrange
        var envelope = new HangfireStepEnvelope { StepKey = "a", StepType = "A", InputsJson = "[1,2,3]" };

        // Act
        var act = () => envelope.ToStepInstance();

        // Assert
        Assert.Throws<JsonException>(act);
    }

    [Fact]
    public async Task RunStepAsync_UnregisteredFlow_Throws()
    {
        // Arrange
        var envelope = new HangfireStepEnvelope { FlowId = Guid.NewGuid(), RunId = Guid.NewGuid(), StepKey = "a", StepType = "A" };

        // Act
        var act = async () => await CreateRunner().RunStepAsync(envelope);

        // Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("is not registered", ex.Message);
        Assert.Empty(_engine.ReceivedCalls());
    }

    [Fact]
    public async Task LegacyOverload_ForJobsEnqueuedBefore2_0_StillDelegatesToTheEngine()
    {
        // Arrange — a 1.17 – 1.x job deserialises into a live context + step pair.
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("a", "A") { RunId = ctx.RunId };

        // Act
#pragma warning disable CS0618 // exercising the back-compat overload on purpose
        await CreateRunner().RunStepAsync(ctx, _flow.Id, step);
#pragma warning restore CS0618

        // Assert
        var (runCtx, runFlow, runStep) = CapturedEngineCall();
        Assert.Same(ctx, runCtx);
        Assert.Same(_flow, runFlow);
        Assert.Same(step, runStep);
    }
    [Fact]
    public void Numbers_RoundTrip_WithoutLosingDigits()
    {
        // Arrange
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("pay", "Pay")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["count"] = 5,
                ["ratio"] = 1.5,
                ["sum"] = 0.1 + 0.2,
                ["tiny"] = 1e-300,
                ["amount"] = 12345678901234567.891m,
                ["huge"] = ulong.MaxValue,
            },
        };

        // Act
        var inputs = HangfireStepEnvelope.From(ctx, _flow.Id, step).ToStepInstance().Inputs;

        // Assert — long and double keep their usual shape; values double would round come back as decimal.
        Assert.Equal(5L, inputs["count"]);
        Assert.Equal(1.5d, inputs["ratio"]);
        Assert.Equal(0.1 + 0.2, Assert.IsType<double>(inputs["sum"]));   // 17-digit double stays double
        Assert.Equal(1e-300, Assert.IsType<double>(inputs["tiny"]));     // below decimal's range stays double
        Assert.Equal(12345678901234567.891m, inputs["amount"]);
        Assert.Equal(18446744073709551615m, inputs["huge"]);
    }

    [Fact]
    public void NonFiniteDoubles_DoNotThrow_AndArriveAsTheirJsonLiteral()
    {
        // Arrange — the envelope is built after the dispatch ledger row is recorded, so it must not throw.
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("calc", "Calc")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?>
            {
                ["nan"] = double.NaN,
                ["inf"] = double.PositiveInfinity,
            },
        };

        // Act
        var inputs = HangfireStepEnvelope.From(ctx, _flow.Id, step).ToStepInstance().Inputs;

        // Assert
        Assert.Equal("NaN", inputs["nan"]);
        Assert.Equal("Infinity", inputs["inf"]);
    }
}
