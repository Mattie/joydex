using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "joydex-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewInstallDefaultsEnabledWithSelectedPoolChannels()
    {
        var path = Path.Combine(_directory, "task-alerts.json");

        var preferences = TaskAlertPreferencesStore.LoadOrCreate(path);

        Assert.True(preferences.Enabled);
        Assert.Equal([1, 2, 4, 5], preferences.Channels);
        Assert.Equal(2, preferences.Bank);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SaveNormalizesAndReplacesSettings()
    {
        var path = Path.Combine(_directory, "task-alerts.json");

        TaskAlertPreferencesStore.Save(path, new TaskAlertPreferences(false, [5, 1, 5]));
        var loaded = TaskAlertPreferencesStore.LoadOrCreate(path);

        Assert.False(loaded.Enabled);
        Assert.Equal([1, 5], loaded.Channels);
        Assert.Equal(2, loaded.Bank);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void NormalizesThrottleBankToSupportedRange()
    {
        var path = Path.Combine(_directory, "task-alerts.json");

        TaskAlertPreferencesStore.Save(path, new TaskAlertPreferences(true, [1], 9));

        Assert.Equal(5, TaskAlertPreferencesStore.LoadOrCreate(path).Bank);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
