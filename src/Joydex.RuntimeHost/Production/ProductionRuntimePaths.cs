namespace Joydex.RuntimeHost.Production;

internal sealed record ProductionRuntimePaths(
    string CompanionConfiguration,
    string DataRoot,
    string VoicePreferences,
    string VoiceActivePreferences,
    string PebbleIndexPreferences,
    string PebbleIndexSecret,
    string PebbleIndexInbox,
    string TaskAlertPreferences,
    string TaskAlertState,
    string VoiceWebViewData,
    string Log,
    string LinkToolProfile,
    string GuardianRecovery,
    string DesktopBridgeHost,
    string JoydexApplication)
{
    public static ProductionRuntimePaths FromCompanionConfiguration(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var companion = Path.GetFullPath(configurationPath.Trim());
        var dataRoot = Path.GetDirectoryName(companion)
            ?? throw new InvalidOperationException("The companion configuration path has no parent directory.");
        var localJoydex = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Joydex");
        var baseDirectory = AppContext.BaseDirectory;
        return new ProductionRuntimePaths(
            companion,
            dataRoot,
            Path.Combine(dataRoot, "voice-pe.json"),
            Path.Combine(dataRoot, "runtime-active-voice-pe.json"),
            Path.Combine(dataRoot, "pebble-index.json"),
            Path.Combine(dataRoot, "pebble-index.secret"),
            Path.Combine(dataRoot, "pebble-index", "inbox"),
            Path.Combine(dataRoot, "task-alerts.json"),
            Path.Combine(localJoydex, "task-alert-state.json"),
            Path.Combine(dataRoot, "webview2-voice"),
            Path.Combine(dataRoot, "joydex.log"),
            Path.Combine(dataRoot, "joydex-linktool.led.json"),
            Path.Combine(dataRoot, "led-guardian-recovery.json"),
            Path.Combine(baseDirectory, "Joydex.DesktopBridgeHost.exe"),
            Path.Combine(baseDirectory, "Joydex.App.exe"));
    }
}
