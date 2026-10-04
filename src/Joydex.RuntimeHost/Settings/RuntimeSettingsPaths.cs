using System.Security.Cryptography;
using System.Text;

namespace Joydex.RuntimeHost.Settings;

public sealed record RuntimeSettingsPaths(
    string Companion,
    string Voice,
    string PebbleIndex,
    string TaskAlerts,
    string Journal)
{
    public static RuntimeSettingsPaths InDataRoot(string dataRoot, string companionFileName = "config.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(companionFileName);
        var root = Path.GetFullPath(dataRoot.Trim());
        var fileName = companionFileName.Trim();
        if (Path.IsPathFullyQualified(fileName)
            || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || fileName is "." or "..")
        {
            throw new ArgumentException(
                "The companion configuration filename must not contain a directory path.",
                nameof(companionFileName));
        }
        return ForConfiguration(Path.Combine(root, fileName));
    }

    public static RuntimeSettingsPaths ForConfiguration(string companionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companionPath);
        var normalizedCompanion = Path.GetFullPath(companionPath.Trim());
        var root = Path.GetDirectoryName(normalizedCompanion)
            ?? throw new ArgumentException(
                "The companion configuration path has no parent directory.",
                nameof(companionPath));
        return new RuntimeSettingsPaths(
            normalizedCompanion,
            Path.Combine(root, "voice-pe.json"),
            Path.Combine(root, "pebble-index.json"),
            Path.Combine(root, "task-alerts.json"),
            Path.Combine(root, "runtime-settings-journal.json"));
    }

    public RuntimeSettingsPaths Normalize() => new(
        Path.GetFullPath(Companion),
        Path.GetFullPath(Voice),
        Path.GetFullPath(PebbleIndex),
        Path.GetFullPath(TaskAlerts),
        Path.GetFullPath(Journal));

    public string For(Joydex.Contracts.SettingsAggregateId aggregate) => aggregate switch
    {
        Joydex.Contracts.SettingsAggregateId.Companion => Companion,
        Joydex.Contracts.SettingsAggregateId.Voice => Voice,
        Joydex.Contracts.SettingsAggregateId.PebbleIndex => PebbleIndex,
        Joydex.Contracts.SettingsAggregateId.TaskAlerts => TaskAlerts,
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
    };

    internal string SelectionIdentity()
    {
        var normalized = Normalize();
        var identity = string.Join(
            '\0',
            "settings-selection-v1",
            normalized.Companion.ToUpperInvariant());
        return "selection-v1-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }
}
