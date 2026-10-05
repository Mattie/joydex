using Joydex.Contracts;

namespace Joydex.RuntimeHost.Plugins;

/// <summary>The fixed catalog of trusted plugins shipped in this Joydex release.</summary>
internal static class BundledPluginCatalog
{
    internal const string PadId = RuntimePluginIds.Pad;
    internal const string VoiceId = RuntimePluginIds.VoicePe;
    internal const string PebbleId = RuntimePluginIds.PebbleIndex;

    public static IReadOnlyList<BundledPluginRegistration> Registrations { get; } =
    [
        new(
            PadId,
            Version: "1.0.0",
            HostApiMajor: 1,
            MinimumHostApiMinor: 0,
            SettingsSchemaVersion: 1,
            Execution: BundledPluginExecutionModel.InProcess),
        new(
            VoiceId,
            Version: "1.0.0",
            HostApiMajor: 1,
            MinimumHostApiMinor: 3,
            SettingsSchemaVersion: 1,
            Execution: BundledPluginExecutionModel.WorkerProcess),
        new(
            PebbleId,
            Version: "1.0.0",
            HostApiMajor: 1,
            MinimumHostApiMinor: 3,
            SettingsSchemaVersion: 1,
            Execution: BundledPluginExecutionModel.WorkerProcess),
    ];

    public static BundledPluginRegistration GetRequired(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return Registrations.SingleOrDefault(candidate => string.Equals(
                   candidate.Id,
                   pluginId,
                   StringComparison.Ordinal))
               ?? throw new KeyNotFoundException($"Bundled plugin '{pluginId}' is not registered.");
    }
}
