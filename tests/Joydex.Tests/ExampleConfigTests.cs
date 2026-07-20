using Joydex.Core.Config;
using Joydex.Core.Mapping;

namespace Joydex.Tests;

public sealed class ExampleConfigTests
{
    [Fact]
    public void StarterExampleMatchesFiveWayShiftStarterProfile()
    {
        var (json, actual) = Load("joydex.example.json");
        var expected = CodexMicroStarterProfile.Create(Cm3ModeDialProfile.FiveWayShift);

        AssertMachineNeutralAndSafe(json, actual);
        Assert.Empty(actual.BankSelectors);

        var device = Assert.Single(actual.Devices);
        Assert.Equal("cm3", device.Id);
        Assert.Equal("VPC Throttle MT-50CM3", device.Selector.ProductNameContains);
        Assert.Empty(device.BankSelectors);
        Assert.Equal("cm3", device.ButtonMapTemplate);
        Assert.Equal("cm3", device.ButtonMapHoldControl?.DeviceId);
        Assert.Equal(CompanionConfig.AlwaysBank, device.ButtonMapHoldControl?.Bank);
        Assert.Equal(36, device.ButtonMapHoldControl?.Button);

        Assert.Equal(
            expected.Bindings.Select(BindingValues),
            actual.Bindings.Select(BindingValues));

        var expectedPicker = Assert.Single(expected.PromptPickers);
        var actualPicker = Assert.Single(actual.PromptPickers);
        Assert.Equal(expectedPicker.Id, actualPicker.Id);
        Assert.Equal(expectedPicker.Name, actualPicker.Name);
        Assert.Equal(expectedPicker.Prompts, actualPicker.Prompts);
        Assert.Equal(expectedPicker.SubmitAfterInsert, actualPicker.SubmitAfterInsert);
        Assert.Equal(expectedPicker.IncludeExitOption, actualPicker.IncludeExitOption);
        Assert.Equal(expectedPicker.DefaultPromptIndex, actualPicker.DefaultPromptIndex);
        Assert.Equal(ControlValues(expectedPicker.Controls.Up), ControlValues(actualPicker.Controls.Up));
        Assert.Equal(ControlValues(expectedPicker.Controls.Down), ControlValues(actualPicker.Controls.Down));
        Assert.Equal(ControlValues(expectedPicker.Controls.Insert), ControlValues(actualPicker.Controls.Insert));
    }

    [Fact]
    public void AdvancedExampleIsSafeAndExercisesMultiDeviceFeatures()
    {
        var (json, config) = Load("joydex.advanced.example.json");

        AssertMachineNeutralAndSafe(json, config);
        Assert.Equal(["cm3", "alpha-warbrd"], config.Devices.Select(device => device.Id));
        Assert.Equal(2, config.PromptPickers.Count);

        var alpha = config.Devices[1];
        Assert.Equal("alpha-warbrd", alpha.ButtonMapTemplate);
        Assert.Equal("cm3", alpha.ButtonMapHoldControl?.DeviceId);
        Assert.Equal(34, alpha.ButtonMapHoldControl?.Button);

        Assert.Contains(config.Bindings, binding => binding.Action == "previous-task");
        Assert.Contains(config.Bindings, binding => binding.Action == "next-task");
        Assert.Contains(config.Bindings, binding => binding.Name == "T5 up - Forward");
        Assert.Contains(config.Bindings, binding => binding.Name == "T5 down - Back");
        Assert.Equal(
            [2, 2],
            config.Bindings
                .Where(binding => binding.Action is "scroll-up" or "scroll-down")
                .Select(binding => binding.WheelNotches));

        var reviewPicker = config.PromptPickers[1];
        Assert.Equal("alpha-warbrd", reviewPicker.Controls.Up.DeviceId);
        Assert.All(reviewPicker.SubmitAfterInsert, Assert.True);
        Assert.True(reviewPicker.IncludeExitOption);
    }

    private static (string Json, CompanionConfig Config) Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", fileName);
        return (File.ReadAllText(path), ConfigStore.LoadOrCreate(path));
    }

    private static void AssertMachineNeutralAndSafe(string json, CompanionConfig config)
    {
        Assert.DoesNotContain("instanceGuid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("productGuid", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(config.Safety.DryRun);
        Assert.True(config.Safety.RequireCodexForeground);
        Assert.All(config.Devices, device =>
        {
            Assert.Null(device.Selector.InstanceGuid);
            Assert.Null(device.Selector.ProductGuid);
        });
    }

    private static object BindingValues(ButtonBinding binding) => new
    {
        binding.Name,
        binding.DeviceId,
        binding.Bank,
        binding.Button,
        binding.Trigger,
        binding.Action,
        binding.WheelNotches,
    };

    private static object ControlValues(DeviceControlReference control) => new
    {
        control.DeviceId,
        control.Bank,
        control.Button,
    };
}
