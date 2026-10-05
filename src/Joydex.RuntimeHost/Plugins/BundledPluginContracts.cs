namespace Joydex.RuntimeHost.Plugins;

/// <summary>Describes one trusted plugin compiled into the Joydex release.</summary>
internal sealed record BundledPluginRegistration(
    string Id,
    string Version,
    int HostApiMajor,
    int MinimumHostApiMinor,
    int SettingsSchemaVersion,
    BundledPluginExecutionModel Execution);

internal enum BundledPluginExecutionModel
{
    InProcess,
    WorkerProcess,
}

/// <summary>Reports the latest lifecycle state of one bundled plugin without domain payloads.</summary>
internal sealed record BundledPluginHealth(
    string PluginId,
    BundledPluginLifecycleState State,
    long Generation,
    string Detail,
    bool CanRestart,
    bool CanReload);

internal enum BundledPluginLifecycleState
{
    Disabled,
    Starting,
    Ready,
    Retrying,
    Blocked,
    Faulted,
    Stopping,
    Stopped,
}
