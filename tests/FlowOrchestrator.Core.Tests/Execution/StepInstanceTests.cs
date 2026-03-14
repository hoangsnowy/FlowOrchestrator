using FlowOrchestrator.Core.Execution;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Execution;

public class StepInstanceTests
{
    [Fact]
    public void Constructor_SetsKeyAndType()
    {
        var instance = new StepInstance("step1", "LogMessage");

        instance.Key.Should().Be("step1");
        instance.Type.Should().Be("LogMessage");
    }

    [Fact]
    public void DefaultInputs_IsEmptyDictionary()
    {
        var instance = new StepInstance("step1", "A");

        instance.Inputs.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var runId = Guid.NewGuid();
        var instance = new StepInstance("step1", "Query")
        {
            RunId = runId,
            PrincipalId = "user-1",
            ScheduledTime = DateTimeOffset.UtcNow,
            Index = 5,
            ScopeMoveNext = true
        };

        instance.RunId.Should().Be(runId);
        instance.PrincipalId.Should().Be("user-1");
        instance.Index.Should().Be(5);
        instance.ScopeMoveNext.Should().BeTrue();
    }
}
