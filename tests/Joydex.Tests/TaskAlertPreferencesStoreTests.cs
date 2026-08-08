using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "joydex-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewInstallDefaultsEnabledWithM2Fallback()
    {
        var path = Path.Combine(_directory, "task-alerts.json");

        var preferences = TaskAlertPreferencesStore.LoadOrCreate(path);

        Assert.True(preferences.Enabled);
        Assert.Equal(2, preferences.Bank);
        Assert.Empty(preferences.Suppressions!);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SaveNormalizesAndReplacesSettings()
    {
        var path = Path.Combine(_directory, "task-alerts.json");

        TaskAlertPreferencesStore.Save(path, new TaskAlertPreferences(false, 9));
        var loaded = TaskAlertPreferencesStore.LoadOrCreate(path);

        Assert.False(loaded.Enabled);
        Assert.Equal(5, loaded.Bank);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void NormalizesThrottleBankToSupportedRange()
    {
        var path = Path.Combine(_directory, "task-alerts.json");

        TaskAlertPreferencesStore.Save(path, new TaskAlertPreferences(true, 9));

        Assert.Equal(5, TaskAlertPreferencesStore.LoadOrCreate(path).Bank);
    }

    [Fact]
    public void LoadsEarlierChannelSettingsWithoutKeepingObsoleteSelection()
    {
        var path = Path.Combine(_directory, "task-alerts.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """
            { "enabled": false, "channels": [1, 2, 4, 5], "bank": 3 }
            """);

        var loaded = TaskAlertPreferencesStore.LoadOrCreate(path);

        Assert.False(loaded.Enabled);
        Assert.Equal(3, loaded.Bank);
    }

    [Fact]
    public void PersistsNormalizedTaskAndWorkspaceSuppressions()
    {
        var path = Path.Combine(_directory, "task-alerts.json");
        var workspace = Path.Combine(_directory, "voice-chat");
        var preferences = new TaskAlertPreferences(
            Suppressions:
            [
                new(TaskAlertSuppressionScope.Task, " task-1 "),
                new(TaskAlertSuppressionScope.Workspace, workspace + Path.DirectorySeparatorChar),
                new(TaskAlertSuppressionScope.Task, "task-1"),
            ]);

        TaskAlertPreferencesStore.Save(path, preferences);
        var loaded = TaskAlertPreferencesStore.LoadOrCreate(path);

        Assert.Collection(
            loaded.Suppressions!,
            rule => Assert.Equal(
                new TaskAlertSuppressionRule(TaskAlertSuppressionScope.Task, "task-1"),
                rule),
            rule => Assert.Equal(
                new TaskAlertSuppressionRule(
                    TaskAlertSuppressionScope.Workspace,
                    Path.GetFullPath(workspace)),
                rule));
        var json = File.ReadAllText(path);
        Assert.Contains("\"scope\": \"task\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scope\": \"workspace\"", json, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
