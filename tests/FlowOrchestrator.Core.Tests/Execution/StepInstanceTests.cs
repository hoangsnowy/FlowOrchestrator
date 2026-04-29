using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Core.Tests.Execution;

public class StepInstanceTests
{
    [Fact]
    public void Constructor_SetsKeyAndType()
    {
        // Arrange

        // Act
        var instance = new StepInstance("step1", "LogMessage");

        // Assert
        Assert.Equal("step1", instance.Key);
        Assert.Equal("LogMessage", instance.Type);
    }

    [Fact]
    public void DefaultInputs_IsEmptyDictionary()
    {
        // Arrange

        // Act
        var instance = new StepInstance("step1", "A");

        // Assert
        Assert.NotNull(instance.Inputs);
        Assert.Empty(instance.Inputs);
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        // Arrange
        var runId = Guid.NewGuid();

        // Act
        var instance = new StepInstance("step1", "Query")
        {
            RunId = runId,
            PrincipalId = "user-1",
            ScheduledTime = DateTimeOffset.UtcNow,
            Index = 5,
            ScopeMoveNext = true
        };

        // Assert
        Assert.Equal(runId, instance.RunId);
        Assert.Equal("user-1", instance.PrincipalId);
        Assert.Equal(5, instance.Index);
        Assert.True(instance.ScopeMoveNext);
    }
}
