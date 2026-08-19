namespace Joydex.Core.TaskAlerts;

public enum TaskAlertLedOutputMode
{
    LinkTool,
    DirectHid,
}

public readonly record struct TaskAlertRgbColor(byte Red, byte Green, byte Blue)
{
    public string ToHex() => $"#{Red:X2}{Green:X2}{Blue:X2}";
}

public sealed record TaskAlertLedPalette(
    string Running = "#555555",
    string Approval = "#FFFF00",
    string Completed = "#004000",
    string Fault = "#FF0000");

public sealed record TaskAlertThrottleBanks(
    string[]? M1 = null,
    string[]? M2 = null,
    string[]? M3 = null,
    string[]? M4 = null,
    string[]? M5 = null);

public sealed record TaskAlertLedOptions(
    int Version = 1,
    TaskAlertLedOutputMode Mode = TaskAlertLedOutputMode.LinkTool,
    TaskAlertLedPalette? TaskColors = null,
    TaskAlertThrottleBanks? ThrottleBanks = null,
    string? AlphaIdle = null)
{
    public const int CurrentVersion = 1;
    public const string FirmwareIdle = "firmware";

    public static TaskAlertLedOptions CreateDefault() => new(
        TaskColors: new TaskAlertLedPalette(),
        ThrottleBanks: new TaskAlertThrottleBanks(
            M1: Bank("#000000", "#000000", "#000000", "#000000", "#000000", "#000000"),
            M2: Bank("#000000", "#000000", "#0000FF", "#000000", "#000000", "#0000FF"),
            M3: Bank("#000000", "#000000", "#00FF00", "#000000", "#000000", "#00FF00"),
            M4: Bank("#000000", "#000000", "#FF0000", "#000000", "#000000", "#FF0000"),
            M5: Bank("#802060", "#802060", "#802060", "#802060", "#802060", "#802060")),
        AlphaIdle: FirmwareIdle);

    public TaskAlertLedOptions Normalize()
    {
        if (Version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported task-alert LED settings version '{Version}'.");
        }

        var defaults = CreateDefault();
        var palette = TaskColors ?? defaults.TaskColors!;
        var normalizedPalette = new TaskAlertLedPalette(
            NormalizeColor(palette.Running, "taskColors.running"),
            NormalizeColor(palette.Approval, "taskColors.approval"),
            NormalizeColor(palette.Completed, "taskColors.completed"),
            NormalizeColor(palette.Fault, "taskColors.fault"));
        var banks = ThrottleBanks ?? defaults.ThrottleBanks!;
        var normalizedBanks = new TaskAlertThrottleBanks(
            NormalizeBank(banks.M1 ?? defaults.ThrottleBanks!.M1!, "throttleBanks.m1"),
            NormalizeBank(banks.M2 ?? defaults.ThrottleBanks!.M2!, "throttleBanks.m2"),
            NormalizeBank(banks.M3 ?? defaults.ThrottleBanks!.M3!, "throttleBanks.m3"),
            NormalizeBank(banks.M4 ?? defaults.ThrottleBanks!.M4!, "throttleBanks.m4"),
            NormalizeBank(banks.M5 ?? defaults.ThrottleBanks!.M5!, "throttleBanks.m5"));
        var alphaIdle = string.IsNullOrWhiteSpace(AlphaIdle) ? FirmwareIdle : AlphaIdle.Trim();
        if (!string.Equals(alphaIdle, FirmwareIdle, StringComparison.OrdinalIgnoreCase))
        {
            alphaIdle = NormalizeColor(alphaIdle, "alphaIdle");
        }
        else
        {
            alphaIdle = FirmwareIdle;
        }

        return this with
        {
            Version = CurrentVersion,
            TaskColors = normalizedPalette,
            ThrottleBanks = normalizedBanks,
            AlphaIdle = alphaIdle,
        };
    }

    public TaskAlertRgbColor ColorFor(TaskAlertState state)
    {
        var palette = Normalize().TaskColors!;
        return ParseColor(state switch
        {
            TaskAlertState.Running => palette.Running,
            TaskAlertState.Approval => palette.Approval,
            TaskAlertState.Completed => palette.Completed,
            TaskAlertState.Fault => palette.Fault,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        });
    }

    public IReadOnlyList<TaskAlertRgbColor> BankColors(int bank)
    {
        var banks = Normalize().ThrottleBanks!;
        var values = bank switch
        {
            1 => banks.M1!,
            2 => banks.M2!,
            3 => banks.M3!,
            4 => banks.M4!,
            5 => banks.M5!,
            _ => throw new ArgumentOutOfRangeException(nameof(bank), "The throttle bank must be M1 through M5."),
        };
        return values.Select(ParseColor).ToArray();
    }

    public TaskAlertRgbColor? AlphaIdleColor()
    {
        var value = Normalize().AlphaIdle!;
        return string.Equals(value, FirmwareIdle, StringComparison.Ordinal)
            ? null
            : ParseColor(value);
    }

    public static TaskAlertRgbColor ParseColor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = NormalizeColor(value, "color");
        return new TaskAlertRgbColor(
            byte.Parse(normalized.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(normalized.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(normalized.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber));
    }

    private static string[] Bank(params string[] values) => values;

    private static string[] NormalizeBank(IReadOnlyList<string> values, string name)
    {
        if (values.Count != 6)
        {
            throw new InvalidDataException($"{name} must contain exactly six colors.");
        }

        return values.Select((value, index) => NormalizeColor(value, $"{name}[{index}]")).ToArray();
    }

    private static string NormalizeColor(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 7
            || value[0] != '#'
            || !value.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"{name} must be a color in #RRGGBB form.");
        }

        return value.ToUpperInvariant();
    }
}
