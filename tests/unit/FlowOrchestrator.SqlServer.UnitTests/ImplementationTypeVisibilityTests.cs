using System.Reflection;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// Locks the 2.0 public surface of <c>FlowOrchestrator.SqlServer</c>: the stores are
/// <see langword="internal"/> and selected with <c>UseSqlServer(cs)</c> /
/// <c>AddSqlServerWebhookHardening(cs)</c>; the migrator stays public, its test hooks do not.
/// </summary>
public sealed class ImplementationTypeVisibilityTests
{
    /// <summary>Stores registered by the SQL Server builder extensions.</summary>
    public static TheoryData<Type> InternalTypes => new()
    {
        typeof(SqlFlowRunStore),
        typeof(SqlFlowStore),
        typeof(SqlFlowScheduleStateStore),
        typeof(SqlServerRunSearchTokenIndex),
        typeof(SqlWebhookRejectionStore),
        typeof(SqlWebhookReplayStore),
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

    /// <summary>The migrator is public; its background-build hook and row limit are not.</summary>
    [Fact]
    public void Migrator_is_public_and_its_test_hooks_are_not()
    {
        // Arrange
        var type = typeof(FlowOrchestratorSqlMigrator);
        const BindingFlags publicMembers = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        // Act
        var backgroundIndexBuild = type.GetProperty("BackgroundIndexBuild", publicMembers);
        var offlineRowLimit = type.GetField("OfflineIndexBuildRowLimit", publicMembers);

        // Assert
        Assert.True(type.IsVisible);
        Assert.Null(backgroundIndexBuild);
        Assert.Null(offlineRowLimit);
    }
}
