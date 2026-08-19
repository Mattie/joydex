using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Core.TaskAlerts;

public enum TaskAlertSuppressionScope
{
    Task,
    Workspace,
}

public sealed record TaskAlertSuppressionRule(TaskAlertSuppressionScope Scope, string Value);

public sealed record TaskAlertPreferences(
    bool Enabled = true,
    int Bank = 2,
    TaskAlertSuppressionRule[]? Suppressions = null,
    TaskAlertLedOptions? LedOutput = null)
{
    public static TaskAlertPreferences Default { get; } = new(
        Suppressions: [],
        LedOutput: TaskAlertLedOptions.CreateDefault());

    public TaskAlertPreferences Normalize()
    {
        var bank = Math.Clamp(Bank, 1, 5);
        var suppressions = (Suppressions ?? [])
            .Select(TaskAlertSuppression.Normalize)
            .Where(rule => rule is not null)
            .Cast<TaskAlertSuppressionRule>()
            .Distinct(TaskAlertSuppression.RuleComparer)
            .Take(TaskAlertSuppression.MaximumRules)
            .ToArray();
        var ledOutput = (LedOutput ?? TaskAlertLedOptions.CreateDefault()).Normalize();
        return this with { Bank = bank, Suppressions = suppressions, LedOutput = ledOutput };
    }
}

public static class TaskAlertPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
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
