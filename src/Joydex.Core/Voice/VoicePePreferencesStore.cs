using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Core.Voice;

public static class VoicePePreferencesStore
{
    private const int MaximumDocumentBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static VoicePePreferences LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            Save(path, VoicePePreferences.Default);
            return VoicePePreferences.Default;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length is <= 0 or > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The Voice PE settings file must contain 1 to {MaximumDocumentBytes} bytes.");
        }

        byte[] documentBytes;
        try
        {
            documentBytes = File.ReadAllBytes(path);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("The Voice PE settings file could not be read.", exception);
        }

        if (documentBytes.Length is <= 0 or > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The Voice PE settings file must contain 1 to {MaximumDocumentBytes} bytes.");
        }

        int schemaVersion;
        try
        {
            using var document = JsonDocument.Parse(documentBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement)
                || !schemaElement.TryGetInt32(out schemaVersion))
            {
                throw new JsonException("The document has no integer schemaVersion.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Voice PE settings file is not valid JSON.", exception);
        }

        VoicePePreferences preferences;
        var migrated = false;
        try
        {
            preferences = schemaVersion switch
            {
                1 => MigrateFromV1(
                    JsonSerializer.Deserialize<VoicePePreferencesV1>(documentBytes, JsonOptions)
                    ?? throw new JsonException("The Voice PE settings file was empty.")),
                2 => MigrateFromV2(
                    JsonSerializer.Deserialize<VoicePePreferencesV2>(documentBytes, JsonOptions)
                    ?? throw new JsonException("The Voice PE settings file was empty.")),
                3 => MigrateFromV3(
                    JsonSerializer.Deserialize<VoicePePreferencesV3>(documentBytes, JsonOptions)
                    ?? throw new JsonException("The Voice PE settings file was empty.")),
                4 => MigrateFromV4(
                    JsonSerializer.Deserialize<VoicePePreferencesV4>(documentBytes, JsonOptions)
                    ?? throw new JsonException("The Voice PE settings file was empty.")),
                5 => MigrateFromV5(
                    JsonSerializer.Deserialize<VoicePePreferencesV5>(documentBytes, JsonOptions)
                    ?? throw new JsonException("The Voice PE settings file was empty.")),
                VoicePePreferences.CurrentSchemaVersion =>
                    JsonSerializer.Deserialize<VoicePePreferences>(documentBytes, JsonOptions)
                    ?? throw new JsonException("The Voice PE settings file was empty."),
                _ => throw new InvalidDataException(
                    $"Unsupported Voice PE settings schema version {schemaVersion}."),
            };
            migrated = schemaVersion != VoicePePreferences.CurrentSchemaVersion;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The Voice PE settings file is not valid schema-v{schemaVersion} JSON.",
                exception);
        }

        var normalized = preferences.Normalize();
        var errors = normalized.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "The Voice PE settings are invalid:" + Environment.NewLine + "- "
                + string.Join(Environment.NewLine + "- ", errors));
        }

        if (migrated)
        {
            if (schemaVersion is 4 or 5)
            {
                PreserveSchemaBackup(path, schemaVersion, documentBytes);
            }
            Save(path, normalized);
        }

        return normalized;
    }

    public static void Save(string path, VoicePePreferences preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(preferences);

        var normalized = preferences.Normalize();
        var errors = normalized.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "The Voice PE settings are invalid:" + Environment.NewLine + "- "
                + string.Join(Environment.NewLine + "- ", errors));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The Voice PE settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                stream.WriteByte((byte)'\n');
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

    private static VoicePePreferences MigrateFromV1(VoicePePreferencesV1 legacy) => new(
        SchemaVersion: VoicePePreferences.CurrentSchemaVersion,
        Enabled: legacy.Enabled,
        DeviceEndpoint: legacy.DeviceEndpoint,
        PinnedTaskId: legacy.PinnedTaskId,
        PinnedTaskLabel: legacy.PinnedTaskLabel,
        SessionMode: VoicePeSessionMode.LastVoiceFallback,
        DedicatedTaskId: "",
        DedicatedTaskLabel: "",
        CodexAppServerPath: "");

    private static VoicePePreferences MigrateFromV2(VoicePePreferencesV2 legacy) => new(
        SchemaVersion: VoicePePreferences.CurrentSchemaVersion,
        Enabled: legacy.Enabled,
        DeviceEndpoint: legacy.DeviceEndpoint,
        PinnedTaskId: legacy.PinnedTaskId,
        PinnedTaskLabel: legacy.PinnedTaskLabel,
        SessionMode: legacy.SessionMode,
        DedicatedTaskId: legacy.DedicatedTaskId,
        DedicatedTaskLabel: legacy.DedicatedTaskLabel,
        CodexAppServerPath: legacy.CodexAppServerPath);

    private static VoicePePreferences MigrateFromV3(VoicePePreferencesV3 legacy) => new(
        SchemaVersion: VoicePePreferences.CurrentSchemaVersion,
        Enabled: legacy.Enabled,
        DeviceEndpoint: legacy.DeviceEndpoint,
        PinnedTaskId: legacy.PinnedTaskId,
        PinnedTaskLabel: legacy.PinnedTaskLabel,
        SessionMode: legacy.SessionMode,
        DedicatedTaskId: legacy.DedicatedTaskId,
        DedicatedTaskLabel: legacy.DedicatedTaskLabel,
        CodexAppServerPath: legacy.CodexAppServerPath,
        RealtimeVoice: legacy.RealtimeVoice,
        ConversationSpeakerGain: legacy.ConversationSpeakerGain,
        PreserveAssistantAudioDiagnostics: false);

    private static VoicePePreferences MigrateFromV4(VoicePePreferencesV4 legacy) => new(
        SchemaVersion: VoicePePreferences.CurrentSchemaVersion,
        Enabled: legacy.Enabled,
        DeviceEndpoint: legacy.DeviceEndpoint,
        PinnedTaskId: legacy.PinnedTaskId,
        PinnedTaskLabel: legacy.PinnedTaskLabel,
        SessionMode: legacy.SessionMode,
        DedicatedTaskId: legacy.DedicatedTaskId,
        DedicatedTaskLabel: legacy.DedicatedTaskLabel,
        CodexAppServerPath: legacy.CodexAppServerPath,
        AgentWorkspacePath: "",
        AgentProjectId: "",
        AgentProjectLabel: "",
        RealtimeVoice: legacy.RealtimeVoice,
        ConversationSpeakerGain: legacy.ConversationSpeakerGain,
        PreserveAssistantAudioDiagnostics: legacy.PreserveAssistantAudioDiagnostics);

    private static VoicePePreferences MigrateFromV5(VoicePePreferencesV5 legacy) => new(
        SchemaVersion: VoicePePreferences.CurrentSchemaVersion,
        Enabled: legacy.Enabled,
        DeviceEndpoint: legacy.DeviceEndpoint,
        PinnedTaskId: legacy.PinnedTaskId,
        PinnedTaskLabel: legacy.PinnedTaskLabel,
        SessionMode: legacy.SessionMode,
        DedicatedTaskId: legacy.DedicatedTaskId,
        DedicatedTaskLabel: legacy.DedicatedTaskLabel,
        CodexAppServerPath: legacy.CodexAppServerPath,
        AgentWorkspacePath: legacy.AgentWorkspacePath,
        AgentProjectId: legacy.AgentProjectId,
        AgentProjectLabel: legacy.AgentProjectLabel,
        RealtimeVoice: legacy.RealtimeVoice,
        ConversationSpeakerGain: legacy.ConversationSpeakerGain,
        PreserveAssistantAudioDiagnostics: legacy.PreserveAssistantAudioDiagnostics);

    private static void PreserveSchemaBackup(string path, int schemaVersion, ReadOnlySpan<byte> documentBytes)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The Voice PE settings path has no parent directory.");
        var backupPath = Path.Combine(directory, $"voice-pe.schema-v{schemaVersion}.backup.json");
        if (File.Exists(backupPath))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var stream = new FileStream(
            backupPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(documentBytes);
        stream.Flush(flushToDisk: true);
    }

    private sealed record VoicePePreferencesV1(
        int SchemaVersion = 1,
        bool Enabled = false,
        string DeviceEndpoint = "",
        string PinnedTaskId = "",
        string PinnedTaskLabel = "");

    private sealed record VoicePePreferencesV2(
        int SchemaVersion = 2,
        bool Enabled = false,
        string DeviceEndpoint = "",
        string PinnedTaskId = "",
        string PinnedTaskLabel = "",
        VoicePeSessionMode SessionMode = VoicePeSessionMode.LastVoiceFallback,
        string DedicatedTaskId = "",
        string DedicatedTaskLabel = "",
        string CodexAppServerPath = "");

    private sealed record VoicePePreferencesV3(
        int SchemaVersion = 3,
        bool Enabled = false,
        string DeviceEndpoint = "",
        string PinnedTaskId = "",
        string PinnedTaskLabel = "",
        VoicePeSessionMode SessionMode = VoicePeSessionMode.LastVoiceFallback,
        string DedicatedTaskId = "",
        string DedicatedTaskLabel = "",
        string CodexAppServerPath = "",
        string RealtimeVoice = "",
        int ConversationSpeakerGain = VoicePePreferences.DefaultConversationSpeakerGain);

    private sealed record VoicePePreferencesV4(
        int SchemaVersion = 4,
        bool Enabled = false,
        string DeviceEndpoint = "",
        string PinnedTaskId = "",
        string PinnedTaskLabel = "",
        VoicePeSessionMode SessionMode = VoicePeSessionMode.LastVoiceFallback,
        string DedicatedTaskId = "",
        string DedicatedTaskLabel = "",
        string CodexAppServerPath = "",
        string RealtimeVoice = "",
        int ConversationSpeakerGain = VoicePePreferences.DefaultConversationSpeakerGain,
        bool PreserveAssistantAudioDiagnostics = false);

    private sealed record VoicePePreferencesV5(
        int SchemaVersion = 5,
        bool Enabled = false,
        string DeviceEndpoint = "",
        string PinnedTaskId = "",
        string PinnedTaskLabel = "",
        VoicePeSessionMode SessionMode = VoicePeSessionMode.LastVoiceFallback,
        string DedicatedTaskId = "",
        string DedicatedTaskLabel = "",
        string CodexAppServerPath = "",
        string AgentWorkspacePath = "",
        string AgentProjectId = "",
        string AgentProjectLabel = "",
        string RealtimeVoice = "",
        int ConversationSpeakerGain = VoicePePreferences.DefaultConversationSpeakerGain,
        bool PreserveAssistantAudioDiagnostics = false);
}
