using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Core.TaskAlerts;

/// <summary>
/// Loads and atomically saves the small, content-free task-alert restart state.
/// </summary>
public static class TaskAlertStateStore
{
    public const int CurrentSchemaVersion = 2;

    public static string LegacyBackupPath(string path) => Path.GetFullPath(path) + ".v1.bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    /// <summary>Loads state from disk, or returns empty state when no file exists.</summary>
    public static TaskAlertPoolState Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return TaskAlertPoolState.Empty;
        }

        var document = JsonSerializer.Deserialize<TaskAlertStateDocument>(
            File.ReadAllText(path),
            JsonOptions)
            ?? throw new InvalidDataException("The task-alert state file was empty.");
        if (document.SchemaVersion is not (1 or CurrentSchemaVersion))
        {
            throw new InvalidDataException(
                $"Unsupported task-alert state schema version {document.SchemaVersion}.");
        }

        if (document.Assignments is null)
        {
            throw new InvalidDataException("The task-alert state has no assignment list.");
        }

        var assignments = document.SchemaVersion == 1
            ? document.Assignments.Select(item => item is null
                ? throw new InvalidDataException("The task-alert state contains an empty assignment.")
                : item with { Identity = null }).ToArray()
            : document.Assignments;
        return new TaskAlertPoolState(assignments,
            document.SchemaVersion == 1 ? null : document.SourceAcknowledgements);
    }

    /// <summary>Saves a complete task-alert state snapshot with atomic replacement.</summary>
    public static void Save(string path, TaskAlertPoolState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The task-alert state path has no parent directory.");
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
                var document = new TaskAlertStateDocument
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Assignments = state.Assignments,
                    SourceAcknowledgements = state.SourceAcknowledgements,
                };
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            var legacy = false;
            if (File.Exists(fullPath))
            {
                using var existing = JsonDocument.Parse(File.ReadAllText(fullPath));
                if (existing.RootElement.ValueKind != JsonValueKind.Object
                    || !existing.RootElement.TryGetProperty("schemaVersion", out var version)
                    || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema))
                    throw new InvalidDataException("The existing task-alert state has no valid schema version.");
                legacy = schema == 1;
            }
            if (legacy && !File.Exists(LegacyBackupPath(fullPath)))
                File.Replace(temporaryPath, fullPath, LegacyBackupPath(fullPath));
            else File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Moves invalid state aside so startup can continue with an empty file.</summary>
    public static string? Quarantine(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The task-alert state path has no parent directory.");
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        var prefix = Path.Combine(
            directory,
            $"{stem}.invalid-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfffffff}");
        var quarantinePath = prefix + extension;
        var suffix = 1;
        while (File.Exists(quarantinePath))
        {
            quarantinePath = $"{prefix}-{suffix++}{extension}";
        }

        File.Move(fullPath, quarantinePath);
        return quarantinePath;
    }

    private sealed class TaskAlertStateDocument
    {
        public int SchemaVersion { get; init; }

        public TaskAlertStoredAssignment[]? Assignments { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TaskAlertSourceAcknowledgement[]? SourceAcknowledgements { get; init; }
    }
}
