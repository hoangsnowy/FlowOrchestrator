using System.Reflection;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Locks the 2.0 public surface of <c>FlowOrchestrator.PostgreSQL</c>: the stores are
/// <see langword="internal"/> and selected with <c>UsePostgreSql(cs)</c> /
/// <c>AddPostgreSqlWebhookHardening(cs)</c>; the migrator stays public, its test hook does not.
/// </summary>
public sealed class ImplementationTypeVisibilityTests
{
    /// <summary>Stores registered by the PostgreSQL builder extensions.</summary>
    public static TheoryData<Type> InternalTypes => new()
    {
        typeof(PostgreSqlFlowRunStore),
        typeof(PostgreSqlFlowScheduleStateStore),
        typeof(PostgreSqlFlowSignalStore),
        typeof(PostgreSqlFlowStore),
        typeof(PostgreSqlOutputsRepository),
        typeof(PostgreSqlWebhookRejectionStore),
        typeof(PostgreSqlWebhookReplayStore),
    };

    /// <summary>No store type is visible outside the package.</summary>
    /// <param name="type">The store type.</param>
    [Theory]
    [MemberData(nameof(InternalTypes))]
    public void Store_type_is_not_public(Type type)
    {
        // Arrange
        var name = type.FullName;

        // Act
        var visible = type.IsVisible;

        // Assert
        Assert.False(visible, $"{name} is public; it was made internal for 2.0.");
    }

    /// <summary>The migrator is public; its background-build hook is not.</summary>
    [Fact]
    public void Migrator_is_public_and_its_test_hook_is_not()
    {
        // Arrange
        var type = typeof(PostgreSqlFlowOrchestratorMigrator);

        // Act
        var backgroundIndexBuild = type.GetProperty("BackgroundIndexBuild", BindingFlags.Public | BindingFlags.Instance);

        // Assert
        Assert.True(type.IsVisible);
        Assert.Null(backgroundIndexBuild);
    }
}
