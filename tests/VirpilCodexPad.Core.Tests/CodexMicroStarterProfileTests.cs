using VirpilCodexPad.Core.Config;
using VirpilCodexPad.Core.Mapping;

namespace VirpilCodexPad.Core.Tests;

public sealed class CodexMicroStarterProfileTests
{
    [Fact]
    public void FiveWayShiftUsesTheInstalledThrottleButtonRanges()
    {
        var profile = CodexMicroStarterProfile.Create(Cm3ModeDialProfile.FiveWayShift);

        Assert.Empty(profile.BankSelectors);
        AssertBinding(profile, button: 56, action: "fast-mode");
        AssertBinding(profile, button: 61, action: "submit");
        AssertBinding(profile, button: 62, action: "plan-mode");
        AssertBinding(profile, button: 67, action: "open-skills");
        AssertBinding(profile, button: 68, action: "agent-1");
        AssertBinding(profile, button: 73, action: "agent-6");
        AssertBinding(profile, button: 52, action: "reasoning-up");
        AssertBinding(profile, button: 51, action: "reasoning-down");
        AssertBinding(profile, button: 54, action: "scroll-down");
        AssertBinding(profile, button: 55, action: "scroll-up");
        AssertBinding(profile, button: 48, action: "home");
        AssertBinding(profile, button: 49, action: "end");
        Assert.DoesNotContain(profile.Bindings, binding => binding.Button == 50);
        Assert.All(profile.Bindings, binding => Assert.Equal(CompanionConfig.AlwaysBank, binding.Bank));
    }

    [Fact]
    public void StandardModeUsesDialButtonsAsBankSelectors()
    {
        var profile = CodexMicroStarterProfile.Create(Cm3ModeDialProfile.StandardButtons);

        Assert.Equal(61, profile.BankSelectors["M2 Commands"]);
        Assert.Equal(62, profile.BankSelectors["M3 Workflows"]);
        Assert.Equal(63, profile.BankSelectors["M4 Agents"]);
        Assert.Contains(profile.Bindings, binding =>
            binding.Bank == "M2 Commands"
            && binding.Button == 42
            && binding.Action == "fast-mode");
    }

    [Theory]
    [InlineData(37)]
    [InlineData(53)]
    [InlineData(60)]
    public void HoldToTalkControlsHavePressAndReleaseBindings(int button)
    {
        var profile = CodexMicroStarterProfile.Create(Cm3ModeDialProfile.FiveWayShift);
        var microphoneBindings = profile.Bindings
            .Where(binding => binding.Action == "push-to-talk" && binding.Button == button)
            .ToArray();

        Assert.Collection(
            microphoneBindings,
            binding => Assert.Equal("press", binding.Trigger),
            binding => Assert.Equal("release", binding.Trigger));
        Assert.Equal(microphoneBindings[0].Button, microphoneBindings[1].Button);
    }

    [Fact]
    public void T3ShowsAndHidesTheButtonMap()
    {
        var profile = CodexMicroStarterProfile.Create(Cm3ModeDialProfile.FiveWayShift);
        var bindings = profile.Bindings
            .Where(binding => binding.Action == "button-map" && binding.Button == 36)
            .ToArray();

        Assert.Collection(
            bindings,
            binding => Assert.Equal("press", binding.Trigger),
            binding => Assert.Equal("release", binding.Trigger));
    }

    private static void AssertBinding(StarterProfile profile, int button, string action) =>
        Assert.Contains(profile.Bindings, binding =>
            binding.Button == button
            && binding.Action == action
            && binding.Trigger == "press");
}
