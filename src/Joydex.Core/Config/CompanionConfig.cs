namespace Joydex.Core.Config;

public sealed class CompanionConfig
{
    public const string AlwaysBank = "always";

    public DeviceSelector Device { get; init; } = new();

    /// <summary>
    /// Configured DirectInput devices. Older configurations use <see cref="Device"/> and are
    /// normalized to a single primary profile when loaded.
    /// </summary>
    public List<DeviceProfile> Devices { get; init; } = [];

    public PollingOptions Polling { get; init; } = new();

    public SafetyOptions Safety { get; init; } = new();

    public OpenWorkingDirectoryOptions OpenWorkingDirectory { get; init; } = new();

    /// <summary>
    /// Maps a bank name to the one-based logical button held by that mode-dial position.
    /// Exactly one configured selector must be held before a binding can fire.
    /// </summary>
    public Dictionary<string, int> BankSelectors { get; init; } = [];

    public List<ButtonBinding> Bindings { get; init; } = [];

    public List<PromptPickerConfig> PromptPickers { get; init; } = [];

    public static CompanionConfig CreateSafeDefault() => new();
}

public sealed class DeviceProfile
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public DeviceSelector Selector { get; init; } = new();

    public Dictionary<string, int> BankSelectors { get; init; } = [];

    /// <summary>Known map template ID, such as "cm3" or "alpha-warbrd".</summary>
    public string? ButtonMapTemplate { get; init; }

    /// <summary>Device-qualified control that shows this device's map while held.</summary>
    public DeviceControlReference? ButtonMapHoldControl { get; init; }

    /// <summary>Legacy same-device hold button; normalized into <see cref="ButtonMapHoldControl"/>.</summary>
    public int? ButtonMapHoldButton { get; init; }
}

public sealed class PromptPickerConfig
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public List<string> Prompts { get; init; } = [];

    /// <summary>Per-prompt flags, aligned by index with <see cref="Prompts"/>.</summary>
    public List<bool> SubmitAfterInsert { get; init; } = [];

    /// <summary>Adds a final picker item that closes the overlay without inserting text.</summary>
    public bool IncludeExitOption { get; init; }

    public int DefaultPromptIndex { get; init; }

    public PromptPickerControls Controls { get; init; } = new();
}

public sealed class PromptPickerControls
{
    public DeviceControlReference Up { get; init; } = new();

    public DeviceControlReference Down { get; init; } = new();

    public DeviceControlReference Insert { get; init; } = new();
}

public sealed class DeviceControlReference
{
    public string DeviceId { get; init; } = string.Empty;

    public string Bank { get; init; } = CompanionConfig.AlwaysBank;

    public int Button { get; init; }
}

public sealed class DeviceSelector
{
    public string ProductNameContains { get; init; } = "VPC Throttle MT-50CM3";

    public string? InstanceGuid { get; init; }

    public string? ProductGuid { get; init; }
}

public sealed class PollingOptions
{
    public int ConnectWarmupMs { get; init; } = 250;

    public int PollIntervalMs { get; init; } = 16;

    public int ReconnectIntervalMs { get; init; } = 2000;

    public int ActionCooldownMs { get; init; } = 250;

    public int AxisTraceThreshold { get; init; } = 512;
}

public sealed class SafetyOptions
{
    public bool DryRun { get; init; } = true;

    public bool RequireCodexForeground { get; init; } = true;

    public string[] CodexProcessNames { get; init; } = ["ChatGPT", "Codex"];

    public string[] SimulatorProcessNames { get; init; } = [];
}

public sealed class OpenWorkingDirectoryOptions
{
    public const string VisualStudioCodeTarget = "vscode";

    public const string FileExplorerTarget = "explorer";

    public string Target { get; init; } = VisualStudioCodeTarget;
}

public sealed class ButtonBinding
{
    public const int DefaultWheelNotches = 1;

    public const int MaximumWheelNotches = 100;

    public required string Name { get; init; }

    /// <summary>Device profile ID. Empty legacy values target the primary device.</summary>
    public string? DeviceId { get; init; }

    public required string Bank { get; init; }

    /// <summary>User-facing, one-based DirectInput logical button number.</summary>
    public int Button { get; init; }

    public string Trigger { get; init; } = "press";

    public required string Action { get; init; }

    /// <summary>Mouse-wheel notches sent for scroll actions.</summary>
    public int WheelNotches { get; init; } = DefaultWheelNotches;
}
