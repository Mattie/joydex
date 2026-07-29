using System.Text.Json;
using Joydex.WirelessPanel;

namespace Joydex.WirelessPanel.Tests;

public sealed class WirelessPanelConfigurationTests
{
    [Fact]
    public void Create_accepts_http_endpoint_with_port_and_path()
    {
        var configuration = WirelessPanelConfiguration.Create(
            " http://panel.local:6052/esphome/ ",
            " panel-user ",
            "correct horse battery staple",
            enabled: true);

        Assert.Equal("http://panel.local:6052/esphome/", configuration.Endpoint.AbsoluteUri);
        Assert.Equal("panel-user", configuration.Username);
        Assert.Equal("correct horse battery staple", configuration.Password);
        Assert.True(configuration.Enabled);
    }

    [Theory]
    [InlineData("https://panel.local/")]
    [InlineData("panel.local")]
    [InlineData("http://user:password@panel.local/")]
    [InlineData("http://@panel.local/")]
    [InlineData("http://panel.local/?mode=touch")]
    [InlineData("http://panel.local/#touch")]
    [InlineData("http://panel.local:0/")]
    [InlineData("ftp://panel.local/")]
    public void Create_rejects_endpoint_outside_attended_direct_boundary(string endpoint)
    {
        Assert.Throws<ArgumentException>(
            () => WirelessPanelConfiguration.Create(endpoint, "user", "password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public void Create_rejects_invalid_username(string username)
    {
        Assert.Throws<ArgumentException>(
            () => WirelessPanelConfiguration.Create("http://panel.local/", username, "password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public void Create_rejects_invalid_password(string password)
    {
        Assert.Throws<ArgumentException>(
            () => WirelessPanelConfiguration.Create("http://panel.local/", "user", password));
    }

    [Fact]
    public void ToString_excludes_password()
    {
        const string password = "marker-password-that-must-stay-secret";
        var configuration = WirelessPanelConfiguration.Create(
            "http://panel.local/",
            "user",
            password);

        var diagnosticText = configuration.ToString();

        Assert.DoesNotContain(password, diagnosticText);
        Assert.Contains("http://panel.local/", diagnosticText);
    }

    [Fact]
    public void Incidental_json_serialization_excludes_password()
    {
        const string password = "json-marker-that-must-stay-secret";
        var configuration = WirelessPanelConfiguration.Create(
            "http://panel.local/",
            "user",
            password);

        var json = JsonSerializer.Serialize(configuration);

        Assert.DoesNotContain(password, json);
        Assert.DoesNotContain("Password", json);
    }
}
