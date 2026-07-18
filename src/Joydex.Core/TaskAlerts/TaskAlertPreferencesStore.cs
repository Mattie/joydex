using System.Text.Json;

namespace Joydex.Core.TaskAlerts;

public sealed record TaskAlertPreferences(bool Enabled = true, int Bank = 2)
{
    public static TaskAlertPreferences Default { get; } = new();

    public TaskAlertPreferences Normalize()
    {
        var bank = Math.Clamp(Bank, 1, 5);
        return this with { Bank = bank };
    }
}

public static class TaskAlertPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static TaskAlertPreferences LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            Save(path, TaskAlertPreferences.Default);
            return TaskAlertPreferences.Default;
        }

        var json = File.ReadAllText(path);
        var preferences = JsonSerializer.Deserialize<TaskAlertPreferences>(json, JsonOptions)
            ?? throw new InvalidDataException("The task-alert settings file was empty.");
        return preferences.Normalize();
    }

    public static void Save(string path, TaskAlertPreferences preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = preferences.Normalize();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The task-alert settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
