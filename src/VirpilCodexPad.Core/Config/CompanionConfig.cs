namespace VirpilCodexPad.Core.Config;

public sealed class CompanionConfig
{
    public const string AlwaysBank = "always";

    public DeviceSelector Device { get; init; } = new();

    public PollingOptions Polling { get; init; } = new();

    public SafetyOptions Safety { get; init; } = new();

    /// <summary>
    /// Maps a bank name to the one-based logical button held by that mode-dial position.
    /// Exactly one configured selector must be held before a binding can fire.
    /// </summary>
    public Dictionary<string, int> BankSelectors { get; init; } = [];

    public List<ButtonBinding> Bindings { get; init; } = [];

    public static CompanionConfig CreateSafeDefault() => new();
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

public sealed class ButtonBinding
{
    public const int DefaultWheelNotches = 1;

    public const int MaximumWheelNotches = 100;

    public required string Name { get; init; }

    public required string Bank { get; init; }

    /// <summary>User-facing, one-based DirectInput logical button number.</summary>
    public int Button { get; init; }

    public string Trigger { get; init; } = "press";

    public required string Action { get; init; }

    /// <summary>Mouse-wheel notches sent for scroll actions.</summary>
    public int WheelNotches { get; init; } = DefaultWheelNotches;
}
