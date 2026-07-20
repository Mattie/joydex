namespace Joydex.Core.Config;

/// <summary>Upgrades legacy single-device configuration into the current runtime shape.</summary>
public static class CompanionConfigNormalizer
{
    public const string PrimaryDeviceId = "cm3";

    public static readonly string[] DefaultPrompts =
    [
        "Explain what you're doing before you work on it.",
        "Make it so.",
        "How much work will this take?",
    ];

    public static CompanionConfig Normalize(CompanionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var legacyMapHold = config.Devices.Count == 0 ? FindLegacyMapHold(config.Bindings) : null;
        var devices = config.Devices.Count > 0
            ? config.Devices.Select(CloneDevice).ToList()
            :
            [
                new DeviceProfile
                {
                    Id = PrimaryDeviceId,
                    DisplayName = DisplayName(config.Device),
                    Selector = config.Device,
                    BankSelectors = new Dictionary<string, int>(config.BankSelectors, StringComparer.OrdinalIgnoreCase),
                    ButtonMapTemplate = InferTemplate(config.Device),
                    ButtonMapHoldControl = legacyMapHold is { } button ? Control(PrimaryDeviceId, button) : null,
                },
            ];

        var primaryId = devices[0].Id;
        var seedDefaultPicker = config.PromptPickers.Count == 0
            && string.Equals(devices[0].ButtonMapTemplate, "cm3", StringComparison.OrdinalIgnoreCase);
        var pickers = seedDefaultPicker
            ? new List<PromptPickerConfig> { CreateDefaultPicker(primaryId, FindDefaultPickerButtons(config, devices[0], primaryId)) }
            : config.PromptPickers.Select(ClonePicker).ToList();

        var bindings = config.Bindings
            .Where(binding => legacyMapHold is null
                || !string.Equals(binding.Action, "button-map", StringComparison.OrdinalIgnoreCase))
            .Select(binding => new ButtonBinding
            {
                Name = binding.Name,
                DeviceId = string.IsNullOrWhiteSpace(binding.DeviceId) ? primaryId : binding.DeviceId,
                Bank = binding.Bank,
                Button = binding.Button,
                Trigger = binding.Trigger,
                Action = binding.Action,
                WheelNotches = binding.WheelNotches,
            })
            .ToList();

        return new CompanionConfig
        {
            Device = devices[0].Selector,
            Devices = devices,
            Polling = config.Polling,
            Safety = config.Safety,
            OpenWorkingDirectory = config.OpenWorkingDirectory,
            BankSelectors = new Dictionary<string, int>(devices[0].BankSelectors, StringComparer.OrdinalIgnoreCase),
            Bindings = bindings,
            PromptPickers = pickers,
        };
    }

    public static PromptPickerConfig CreateDefaultPicker(string deviceId) => new()
    {
        Id = "picker-1",
        Name = "Quick prompts",
        Prompts = [.. DefaultPrompts],
        SubmitAfterInsert = DefaultPrompts.Select(_ => false).ToList(),
        DefaultPromptIndex = 1,
        Controls = new PromptPickerControls
        {
            Up = Control(deviceId, 3),
            Down = Control(deviceId, 2),
            Insert = Control(deviceId, 1),
        },
    };

    private static PromptPickerConfig CreateDefaultPicker(
        string deviceId,
        (int Up, int Down, int Insert) buttons) => new()
        {
            Id = "picker-1",
            Name = "Quick prompts",
            Prompts = [.. DefaultPrompts],
            SubmitAfterInsert = DefaultPrompts.Select(_ => false).ToList(),
            DefaultPromptIndex = 1,
            Controls = new PromptPickerControls
            {
                Up = Control(deviceId, buttons.Up),
                Down = Control(deviceId, buttons.Down),
                Insert = Control(deviceId, buttons.Insert),
            },
        };

    public static DeviceControlReference Control(string deviceId, int button) => new()
    {
        DeviceId = deviceId,
        Bank = CompanionConfig.AlwaysBank,
        Button = button,
    };

    public static string? InferTemplate(DeviceSelector selector)
    {
        var name = selector.ProductNameContains ?? string.Empty;
        if (name.Contains("CM3", StringComparison.OrdinalIgnoreCase))
        {
            return "cm3";
        }

        return name.Contains("WarBRD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Constellation", StringComparison.OrdinalIgnoreCase)
            ? "alpha-warbrd"
            : null;
    }

    private static DeviceProfile CloneDevice(DeviceProfile device) => new()
    {
        Id = device.Id,
        DisplayName = device.DisplayName,
        Selector = device.Selector,
        BankSelectors = new Dictionary<string, int>(device.BankSelectors, StringComparer.OrdinalIgnoreCase),
        ButtonMapTemplate = device.ButtonMapTemplate ?? InferTemplate(device.Selector),
        ButtonMapHoldControl = device.ButtonMapHoldControl is not null
            ? CloneControl(device.ButtonMapHoldControl)
            : device.ButtonMapHoldButton is { } button
                ? Control(device.Id, button)
                : null,
    };

    private static PromptPickerConfig ClonePicker(PromptPickerConfig picker) => new()
    {
        Id = picker.Id,
        Name = picker.Name,
        Prompts = [.. picker.Prompts],
        SubmitAfterInsert = NormalizeSubmitFlags(picker),
        IncludeExitOption = picker.IncludeExitOption,
        DefaultPromptIndex = picker.DefaultPromptIndex,
        Controls = new PromptPickerControls
        {
            Up = CloneControl(picker.Controls.Up),
            Down = CloneControl(picker.Controls.Down),
            Insert = CloneControl(picker.Controls.Insert),
        },
    };

    private static DeviceControlReference CloneControl(DeviceControlReference control) => new()
    {
        DeviceId = control.DeviceId,
        Bank = control.Bank,
        Button = control.Button,
    };

    private static List<bool> NormalizeSubmitFlags(PromptPickerConfig picker) => Enumerable
        .Range(0, picker.Prompts.Count)
        .Select(index => index < picker.SubmitAfterInsert.Count && picker.SubmitAfterInsert[index])
        .ToList();

    private static string DisplayName(DeviceSelector selector) =>
        string.IsNullOrWhiteSpace(selector.ProductNameContains)
            ? "Primary controller"
            : selector.ProductNameContains;

    private static int? FindLegacyMapHold(IEnumerable<ButtonBinding> bindings) => bindings
        .FirstOrDefault(binding => string.Equals(binding.Action, "button-map", StringComparison.OrdinalIgnoreCase)
            && string.Equals(binding.Trigger, "press", StringComparison.OrdinalIgnoreCase))
        ?.Button;

    private static (int Up, int Down, int Insert) FindDefaultPickerButtons(
        CompanionConfig config,
        DeviceProfile primaryDevice,
        string primaryDeviceId)
    {
        var occupied = new HashSet<int>(primaryDevice.BankSelectors.Values);
        if (primaryDevice.ButtonMapHoldControl is { } mapControl
            && string.Equals(mapControl.DeviceId, primaryDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            occupied.Add(mapControl.Button);
        }

        foreach (var binding in config.Bindings.Where(binding =>
                     (string.IsNullOrWhiteSpace(binding.DeviceId)
                         || string.Equals(binding.DeviceId, primaryDeviceId, StringComparison.OrdinalIgnoreCase))
                     && string.Equals(binding.Trigger, "press", StringComparison.OrdinalIgnoreCase)))
        {
            occupied.Add(binding.Button);
        }

        var preferred = new[] { 3, 2, 1 };
        var selected = new int[preferred.Length];
        for (var index = 0; index < preferred.Length; index++)
        {
            if (!occupied.Contains(preferred[index]))
            {
                selected[index] = preferred[index];
                occupied.Add(preferred[index]);
            }
        }

        for (var index = 0; index < selected.Length; index++)
        {
            if (selected[index] == 0)
            {
                selected[index] = Enumerable.Range(1, 128).First(candidate => !occupied.Contains(candidate));
                occupied.Add(selected[index]);
            }
        }

        return (selected[0], selected[1], selected[2]);
    }
}
