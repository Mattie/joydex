using VirpilCodexPad.Core.Config;

namespace VirpilCodexPad.Core.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void SafeDefaultIsValidAndDryRunIsEnabled()
    {
        var config = CompanionConfig.CreateSafeDefault();

        Assert.True(config.Safety.DryRun);
        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void RejectsActionOutsideTheSafeCatalog()
    {
        var config = CreateConfig(
            new ButtonBinding
            {
                Name = "dangerous",
                Bank = "work",
                Button = 3,
                Action = "launch-missiles",
            });

        var error = Assert.Single(ConfigValidator.Validate(config));

        Assert.Contains("unsupported action 'launch-missiles'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateButtonsWithinABank()
    {
        var config = CreateConfig(
            new ButtonBinding
            {
                Name = "first",
                Bank = "work",
                Button = 3,
                Action = "new-task",
            },
            new ButtonBinding
            {
                Name = "second",
                Bank = "work",
                Button = 3,
                Action = "toggle-sidebar",
            });

        Assert.Contains(
            ConfigValidator.Validate(config),
            error => error.Contains("more than one press binding for button 3", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsDuplicateBankSelectorButtons()
    {
        var config = new CompanionConfig
        {
            BankSelectors = new Dictionary<string, int>
            {
                ["work"] = 10,
                ["navigate"] = 10,
            },
        };

        Assert.Contains(
            ConfigValidator.Validate(config),
            error => error.Contains("assigned to more than one bank selector", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsMalformedDeviceGuid()
    {
        var config = new CompanionConfig
        {
            Device = new DeviceSelector { InstanceGuid = "not-a-guid" },
        };

        Assert.Contains(
            ConfigValidator.Validate(config),
            error => error.Contains("device.instanceGuid must be a valid GUID", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsActionBindingThatReusesASelectorButton()
    {
        var config = CreateConfig(
            new ButtonBinding
            {
                Name = "selector-action",
                Bank = "work",
                Button = 10,
                Action = "new-task",
            });

        Assert.Contains(
            ConfigValidator.Validate(config),
            error => error.Contains("reuses bank selector button 10", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsDisablingTheForegroundGuard()
    {
        var config = new CompanionConfig
        {
            Safety = new SafetyOptions { RequireCodexForeground = false },
        };

        Assert.Contains(
            ConfigValidator.Validate(config),
            error => error.Contains("must remain true", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void RejectsScrollWheelNotchesOutsideTheSupportedRange(int wheelNotches)
    {
        var config = CreateConfig(
            new ButtonBinding
            {
                Name = "scroll",
                Bank = "work",
                Button = 3,
                Action = "scroll-down",
                WheelNotches = wheelNotches,
            });

        Assert.Contains(
            ConfigValidator.Validate(config),
            error => error.Contains("wheelNotches must be between 1 and 100", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsConfiguredScrollWheelNotches()
    {
        var config = CreateConfig(
            new ButtonBinding
            {
                Name = "scroll",
                Bank = "work",
                Button = 3,
                Action = "scroll-down",
                WheelNotches = 5,
            });

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void AcceptsPressAndReleaseBindingsForTheButtonMap()
    {
        var config = CreateConfig(
            new ButtonBinding
            {
                Name = "show-map",
                Bank = "work",
                Button = 3,
                Trigger = "press",
                Action = "button-map",
            },
            new ButtonBinding
            {
                Name = "hide-map",
                Bank = "work",
                Button = 3,
                Trigger = "release",
                Action = "button-map",
            });

        Assert.Empty(ConfigValidator.Validate(config));
    }

    private static CompanionConfig CreateConfig(params ButtonBinding[] bindings) => new()
    {
        BankSelectors = new Dictionary<string, int> { ["work"] = 10 },
        Bindings = [.. bindings],
    };
}
