using Joydex.Core.Voice;
using System.Text.Json;

namespace Joydex.Tests;

public sealed class VoicePePreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "joydex-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewInstallIsDisabledAndDoesNotGuessATask()
    {
        var path = Path.Combine(_directory, "voice-pe.json");

        var preferences = VoicePePreferencesStore.LoadOrCreate(path);

        Assert.False(preferences.Enabled);
        Assert.Empty(preferences.PinnedTaskId);
        Assert.Empty(preferences.DeviceEndpoint);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SavesNormalizedPinnedTaskAndEndpoint()
    {
        var path = Path.Combine(_directory, "voice-pe.json");
        var taskId = Guid.NewGuid();
        var preferences = new VoicePePreferences(
            Enabled: true,
            DeviceEndpoint: " http://voice-pe.local:8080/ ",
            PinnedTaskId: $"codex://threads/{taskId:D}",
            PinnedTaskLabel: " Codex Voice Chat ");

        VoicePePreferencesStore.Save(path, preferences);
        var loaded = VoicePePreferencesStore.LoadOrCreate(path);

        Assert.True(loaded.Enabled);
        Assert.Equal("http://voice-pe.local:8080/", loaded.DeviceEndpoint);
        Assert.Equal(taskId.ToString("D"), loaded.PinnedTaskId);
        Assert.Equal("Codex Voice Chat", loaded.PinnedTaskLabel);
        Assert.False(Directory.EnumerateFiles(_directory, "*.tmp").Any());
    }

    [Fact]
    public void MigratesSchemaOneToLastVoiceFallbackWithoutLosingTaskLabel()
    {
        var path = Path.Combine(_directory, "voice-pe.json");
        var taskId = Guid.NewGuid();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 1,
              "enabled": true,
              "deviceEndpoint": "http://voice-pe.local/",
              "pinnedTaskId": "{{taskId:D}}",
              "pinnedTaskLabel": "Codex Voice Chat"
            }
            """);

        var preferences = VoicePePreferencesStore.LoadOrCreate(path);

        Assert.Equal(VoicePePreferences.CurrentSchemaVersion, preferences.SchemaVersion);
        Assert.Equal(VoicePeSessionMode.LastVoiceFallback, preferences.SessionMode);
        Assert.Equal(taskId.ToString("D"), preferences.PinnedTaskId);
        Assert.Equal("Codex Voice Chat", preferences.PinnedTaskLabel);
        Assert.Empty(preferences.DedicatedTaskId);
        using var migrated = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(VoicePePreferences.CurrentSchemaVersion, migrated.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("lastVoiceFallback", migrated.RootElement.GetProperty("sessionMode").GetString());
    }

    [Fact]
    public void SavesJoydexOwnerSettingsWithoutChangingFallback()
    {
        var path = Path.Combine(_directory, "voice-pe.json");
        var fallbackTaskId = Guid.NewGuid();
        var dedicatedTaskId = Guid.NewGuid();
        var appServerPath = Path.Combine(_directory, "codex.exe");
        var workspacePath = Path.Combine(_directory, "voice-workspace");
        var preferences = new VoicePePreferences(
            Enabled: true,
            DeviceEndpoint: "http://voice-pe.local/",
            PinnedTaskId: fallbackTaskId.ToString("D"),
            PinnedTaskLabel: "Codex Voice Chat",
            SessionMode: VoicePeSessionMode.JoydexOwner,
            DedicatedTaskId: $"codex://threads/{dedicatedTaskId:D}",
            DedicatedTaskLabel: " Joydex Voice Chat ",
            CodexAppServerPath: appServerPath,
            AgentWorkspacePath: workspacePath + Path.DirectorySeparatorChar,
            AgentProjectId: " project-123 ",
            AgentProjectLabel: " Joydex Voice ",
            RealtimeVoice: " Cove ",
            ConversationSpeakerGain: 2);

        VoicePePreferencesStore.Save(path, preferences);
        var loaded = VoicePePreferencesStore.LoadOrCreate(path);

        Assert.Equal(VoicePeSessionMode.JoydexOwner, loaded.SessionMode);
        Assert.Equal(dedicatedTaskId.ToString("D"), loaded.DedicatedTaskId);
        Assert.Equal("Joydex Voice Chat", loaded.DedicatedTaskLabel);
        Assert.Equal(Path.GetFullPath(appServerPath), loaded.CodexAppServerPath);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath)), loaded.AgentWorkspacePath);
        Assert.Equal("project-123", loaded.AgentProjectId);
        Assert.Equal("Joydex Voice", loaded.AgentProjectLabel);
        Assert.Equal("cove", loaded.RealtimeVoice);
        Assert.Equal(2, loaded.ConversationSpeakerGain);
        Assert.Equal(fallbackTaskId.ToString("D"), loaded.PinnedTaskId);
        Assert.Equal("Codex Voice Chat", loaded.PinnedTaskLabel);
    }

    [Fact]
    public void MigratesSchemaTwoWithSafeAudioDefaults()
    {
        var path = Path.Combine(_directory, "voice-pe.json");
        var taskId = Guid.NewGuid();
        var appServerPath = Path.Combine(_directory, "codex.exe");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 2,
              "enabled": true,
              "deviceEndpoint": "http://voice-pe.local/",
              "pinnedTaskId": "",
              "pinnedTaskLabel": "",
              "sessionMode": "joydexOwner",
              "dedicatedTaskId": "{{taskId:D}}",
              "dedicatedTaskLabel": "Joydex Voice Chat",
              "codexAppServerPath": "{{appServerPath.Replace("\\", "\\\\")}}"
            }
            """);

        var preferences = VoicePePreferencesStore.LoadOrCreate(path);

        Assert.Equal(VoicePePreferences.CurrentSchemaVersion, preferences.SchemaVersion);
        Assert.False(preferences.Enabled);
        Assert.Empty(preferences.RealtimeVoice);
        Assert.Equal(VoicePePreferences.DefaultConversationSpeakerGain, preferences.ConversationSpeakerGain);
        Assert.False(preferences.PreserveAssistantAudioDiagnostics);
    }

    [Fact]
    public void MigratesSchemaThreeWithAudioCaptureDisabled()
    {
        var path = Path.Combine(_directory, "voice-pe.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """
            {
              "schemaVersion": 3,
              "enabled": false,
              "deviceEndpoint": "",
              "pinnedTaskId": "",
              "pinnedTaskLabel": "",
              "sessionMode": "lastVoiceFallback",
              "dedicatedTaskId": "",
              "dedicatedTaskLabel": "",
              "codexAppServerPath": "",
              "realtimeVoice": "",
              "conversationSpeakerGain": 2
            }
            """);

        var preferences = VoicePePreferencesStore.LoadOrCreate(path);

        Assert.Equal(VoicePePreferences.CurrentSchemaVersion, preferences.SchemaVersion);
        Assert.False(preferences.PreserveAssistantAudioDiagnostics);
        using var migrated = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(migrated.RootElement.GetProperty("preserveAssistantAudioDiagnostics").GetBoolean());
    }

    [Fact]
    public void MigratesSchemaFourAndKeepsOneOriginalBackup()
    {
        var path = Path.Combine(_directory, "voice-pe.json");
        var taskId = Guid.NewGuid();
        Directory.CreateDirectory(_directory);
        var original = $$"""
            {
              "schemaVersion": 4,
              "enabled": true,
              "deviceEndpoint": "http://voice-pe.local/",
              "pinnedTaskId": "",
              "pinnedTaskLabel": "",
              "sessionMode": "joydexOwner",
              "dedicatedTaskId": "{{taskId:D}}",
              "dedicatedTaskLabel": "Old owned task",
              "codexAppServerPath": "C:\\Codex\\codex.exe",
              "realtimeVoice": "cove",
              "conversationSpeakerGain": 2,
              "preserveAssistantAudioDiagnostics": true
            }
            """;
        File.WriteAllText(path, original);

        var preferences = VoicePePreferencesStore.LoadOrCreate(path);

        Assert.Equal(VoicePePreferences.CurrentSchemaVersion, preferences.SchemaVersion);
        Assert.False(preferences.Enabled);
        Assert.Empty(preferences.AgentWorkspacePath);
        Assert.Empty(preferences.AgentProjectId);
        Assert.Empty(preferences.AgentProjectLabel);
        var backup = Path.Combine(_directory, "voice-pe.schema-v4.backup.json");
        Assert.Equal(original, File.ReadAllText(backup));

        File.WriteAllText(path, File.ReadAllText(path));
        _ = VoicePePreferencesStore.LoadOrCreate(path);
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [Fact]
    public void MigratesSchemaFiveWithDesktopMessagingDisabledAndKeepsBackup()
    {
        var path = Path.Combine(_directory, "voice-pe.json");
        Directory.CreateDirectory(_directory);
        var original = """
            {
              "schemaVersion": 5,
              "enabled": false,
              "deviceEndpoint": "",
              "pinnedTaskId": "",
              "pinnedTaskLabel": "",
              "sessionMode": "lastVoiceFallback",
              "dedicatedTaskId": "",
              "dedicatedTaskLabel": "",
              "codexAppServerPath": "",
              "agentWorkspacePath": "",
              "agentProjectId": "",
              "agentProjectLabel": "",
              "realtimeVoice": "",
              "conversationSpeakerGain": 2,
              "preserveAssistantAudioDiagnostics": false
            }
            """;
        File.WriteAllText(path, original);

        var preferences = VoicePePreferencesStore.LoadOrCreate(path);

        Assert.Equal(6, preferences.SchemaVersion);
        Assert.False(preferences.DesktopTaskMessagingEnabled);
        Assert.Empty(preferences.VoiceTargetTaskId);
        Assert.Equal(original, File.ReadAllText(Path.Combine(_directory, "voice-pe.schema-v5.backup.json")));
    }

    [Fact]
    public void MigratesEnabledSchemaFiveOwnerWithoutWorkspaceAsDisabled()
    {
        var path = Path.Combine(_directory, "voice-pe.json");
        var taskId = Guid.NewGuid();
        Directory.CreateDirectory(_directory);
        var original = $$"""
            {
              "schemaVersion": 5,
              "enabled": true,
              "deviceEndpoint": "http://voice-pe.local/",
              "pinnedTaskId": "",
              "pinnedTaskLabel": "",
              "sessionMode": "joydexOwner",
              "dedicatedTaskId": "{{taskId:D}}",
              "dedicatedTaskLabel": "Old owned task",
              "codexAppServerPath": "C:\\Codex\\codex.exe",
              "agentWorkspacePath": "",
              "agentProjectId": "",
              "agentProjectLabel": "",
              "realtimeVoice": "cove",
              "conversationSpeakerGain": 2,
              "preserveAssistantAudioDiagnostics": false
            }
            """;
        File.WriteAllText(path, original);

        var preferences = VoicePePreferencesStore.LoadOrCreate(path);

        Assert.False(preferences.Enabled);
        Assert.Equal(taskId.ToString("D"), preferences.DedicatedTaskId);
        Assert.Empty(preferences.AgentWorkspacePath);
        Assert.Equal(original, File.ReadAllText(Path.Combine(_directory, "voice-pe.schema-v5.backup.json")));
    }

    [Fact]
    public void DesktopMessagingRequiresOwnerModeAndCompleteTargetIdentity()
    {
        var errors = (VoicePePreferences.Default with
        {
            DesktopTaskMessagingEnabled = true,
            VoiceTargetTaskId = Guid.NewGuid().ToString("D"),
        }).Validate();

        Assert.Contains(errors, error => error.Contains("owner mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("selected together", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void JoydexOwnerRequiresDistinctDedicatedTaskAndAppServerPath()
    {
        var taskId = Guid.NewGuid().ToString("D");
        var errors = new VoicePePreferences(
            Enabled: true,
            DeviceEndpoint: "http://voice-pe.local/",
            PinnedTaskId: taskId,
            SessionMode: VoicePeSessionMode.JoydexOwner,
            DedicatedTaskId: taskId).Validate();

        Assert.Contains(errors, error => error.Contains("App Server", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("Workspace", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("must be different", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("codex://threads/not-a-guid")]
    [InlineData("codex://settings/00000000-0000-0000-0000-000000000000")]
    [InlineData("codex://threads/00000000-0000-0000-0000-000000000000?recent=true")]
    public void RejectsNonTaskDeepLinks(string reference)
    {
        Assert.False(CodexTaskReference.TryParse(reference, out _));
    }

    [Fact]
    public void EnabledBridgeRequiresEndpointAndPinnedTask()
    {
        var errors = new VoicePePreferences(Enabled: true).Validate();

        Assert.Contains(errors, error => error.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("pinned Codex task", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EndpointRejectsPathsThatWouldChangeTheEventsRoute()
    {
        Assert.False(VoicePeEndpoint.TryParse("http://voice-pe.local/unexpected", out _));
    }

    [Fact]
    public void RejectsUnsupportedSchemaInsteadOfSilentlyUpgradingIt()
    {
        var path = Path.Combine(_directory, "future.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """
            { "schemaVersion": 99, "enabled": false, "deviceEndpoint": "", "pinnedTaskId": "", "pinnedTaskLabel": "" }
            """);

        var exception = Assert.Throws<InvalidDataException>(() => VoicePePreferencesStore.LoadOrCreate(path));

        Assert.Contains("schema version 99", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
