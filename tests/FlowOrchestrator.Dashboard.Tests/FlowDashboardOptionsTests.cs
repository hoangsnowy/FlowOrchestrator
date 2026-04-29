namespace FlowOrchestrator.Dashboard.Tests;

public sealed class FlowDashboardOptionsTests
{
    [Fact]
    public void IsEnabled_false_when_neither_username_nor_password_set()
    {
        // Arrange

        // Act
        var opts = new FlowDashboardBasicAuthOptions();

        // Assert
        Assert.False(opts.IsEnabled);
    }

    [Fact]
    public void IsEnabled_false_when_only_username_set()
    {
        // Arrange

        // Act
        var opts = new FlowDashboardBasicAuthOptions { Username = "admin" };

        // Assert
        Assert.False(opts.IsEnabled);
    }

    [Fact]
    public void IsEnabled_false_when_only_password_set()
    {
        // Arrange

        // Act
        var opts = new FlowDashboardBasicAuthOptions { Password = "secret" };

        // Assert
        Assert.False(opts.IsEnabled);
    }

    [Fact]
    public void IsEnabled_true_when_both_username_and_password_set()
    {
        // Arrange

        // Act
        var opts = new FlowDashboardBasicAuthOptions { Username = "admin", Password = "secret" };

        // Assert
        Assert.True(opts.IsEnabled);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("  ", "secret")]
    [InlineData("admin", "")]
    [InlineData("admin", "  ")]
    public void IsEnabled_false_when_credentials_are_whitespace(string username, string password)
    {
        // Arrange

        // Act
        var opts = new FlowDashboardBasicAuthOptions { Username = username, Password = password };

        // Assert
        Assert.False(opts.IsEnabled);
    }

    [Fact]
    public void Default_realm_is_FlowOrchestrator_Dashboard()
    {
        // Arrange

        // Act
        var opts = new FlowDashboardBasicAuthOptions();

        // Assert
        Assert.Equal("FlowOrchestrator Dashboard", opts.Realm);
    }

    [Fact]
    public void Default_branding_title_is_set()
    {
        // Arrange

        // Act
        var opts = new FlowDashboardBrandingOptions();

        // Assert
        Assert.Equal("FlowOrchestrator Dashboard", opts.Title);
        Assert.Equal("Dashboard", opts.Subtitle);
        Assert.Null(opts.LogoUrl);
    }

    [Fact]
    public void FlowDashboardOptions_default_section_name_is_FlowDashboard()
    {
        // Arrange

        // Act
        var defaultName = FlowDashboardOptions.DefaultSectionName;

        // Assert
        Assert.Equal("FlowDashboard", defaultName);
    }
}
