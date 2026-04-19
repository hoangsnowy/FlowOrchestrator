namespace FlowOrchestrator.Dashboard.Tests;

public sealed class FlowDashboardOptionsTests
{
    [Fact]
    public void IsEnabled_false_when_neither_username_nor_password_set()
    {
        var opts = new FlowDashboardBasicAuthOptions();
        opts.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_false_when_only_username_set()
    {
        var opts = new FlowDashboardBasicAuthOptions { Username = "admin" };
        opts.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_false_when_only_password_set()
    {
        var opts = new FlowDashboardBasicAuthOptions { Password = "secret" };
        opts.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_true_when_both_username_and_password_set()
    {
        var opts = new FlowDashboardBasicAuthOptions { Username = "admin", Password = "secret" };
        opts.IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("  ", "secret")]
    [InlineData("admin", "")]
    [InlineData("admin", "  ")]
    public void IsEnabled_false_when_credentials_are_whitespace(string username, string password)
    {
        var opts = new FlowDashboardBasicAuthOptions { Username = username, Password = password };
        opts.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Default_realm_is_FlowOrchestrator_Dashboard()
    {
        var opts = new FlowDashboardBasicAuthOptions();
        opts.Realm.Should().Be("FlowOrchestrator Dashboard");
    }

    [Fact]
    public void Default_branding_title_is_set()
    {
        var opts = new FlowDashboardBrandingOptions();
        opts.Title.Should().Be("FlowOrchestrator Dashboard");
        opts.Subtitle.Should().Be("Dashboard");
        opts.LogoUrl.Should().BeNull();
    }

    [Fact]
    public void FlowDashboardOptions_default_section_name_is_FlowDashboard()
    {
        FlowDashboardOptions.DefaultSectionName.Should().Be("FlowDashboard");
    }
}
