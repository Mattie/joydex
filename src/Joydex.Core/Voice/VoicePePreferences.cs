namespace Joydex.Core.Voice;

public enum VoicePeSessionMode
{
    LastVoiceFallback,
    JoydexOwner,
}

/// <summary>
/// User-owned settings for the controller-independent Room Voice integration.
/// </summary>
public sealed record VoicePePreferences(
    int SchemaVersion = VoicePePreferences.CurrentSchemaVersion,
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
    bool PreserveAssistantAudioDiagnostics = false,
    bool DesktopTaskMessagingEnabled = false,
    string VoiceTargetTaskId = "",
    string VoiceTargetHostId = "",
    string VoiceTargetTaskLabel = "")
{
    public const int CurrentSchemaVersion = 6;
    public const int MaximumLabelLength = 120;
    public const int MinimumConversationSpeakerGain = 1;
    public const int MaximumConversationSpeakerGain = 4;
    public const int DefaultConversationSpeakerGain = 2;

    public static IReadOnlyList<string> SupportedRealtimeVoices { get; } =
    [
        // Joydex's Realtime V3 (Frameless Bidi) session uses Codex's V1 voice catalog.
        "arbor", "breeze", "cove", "ember", "juniper", "maple", "sol", "spruce", "vale",
    ];

    public static VoicePePreferences Default { get; } = new();

    public VoicePePreferences Normalize()
    {
        var taskId = CodexTaskReference.TryParse(PinnedTaskId, out var parsedTaskId)
            ? parsedTaskId
            : PinnedTaskId.Trim();
        var dedicatedTaskId = CodexTaskReference.TryParse(DedicatedTaskId, out var parsedDedicatedTaskId)
            ? parsedDedicatedTaskId
            : DedicatedTaskId.Trim();
        var voiceTargetTaskId = CodexTaskReference.TryParse(VoiceTargetTaskId, out var parsedVoiceTargetTaskId)
            ? parsedVoiceTargetTaskId
            : VoiceTargetTaskId.Trim();
        var endpoint = VoicePeEndpoint.TryParse(DeviceEndpoint, out var parsedEndpoint)
            ? parsedEndpoint.AbsoluteUri
            : DeviceEndpoint.Trim();
        var appServerPath = NormalizeAppServerPath(CodexAppServerPath);
        var workspacePath = NormalizeFileSystemPath(AgentWorkspacePath);

        return this with
        {
            DeviceEndpoint = endpoint,
            PinnedTaskId = taskId,
            PinnedTaskLabel = PinnedTaskLabel.Trim(),
            DedicatedTaskId = dedicatedTaskId,
            DedicatedTaskLabel = DedicatedTaskLabel.Trim(),
            CodexAppServerPath = appServerPath,
            AgentWorkspacePath = workspacePath,
            AgentProjectId = AgentProjectId.Trim(),
            AgentProjectLabel = AgentProjectLabel.Trim(),
            RealtimeVoice = RealtimeVoice.Trim().ToLowerInvariant(),
            VoiceTargetTaskId = voiceTargetTaskId,
            VoiceTargetHostId = VoiceTargetHostId.Trim(),
            VoiceTargetTaskLabel = VoiceTargetTaskLabel.Trim(),
        };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"Unsupported Voice PE settings schema version {SchemaVersion}.");
        }

        if (!Enum.IsDefined(SessionMode))
        {
            errors.Add($"Unsupported Voice PE session mode {SessionMode}.");
        }

        errors.AddRange(ValidatePinnedTask(
            required: Enabled && SessionMode == VoicePeSessionMode.LastVoiceFallback,
            includeSchemaVersion: false));
        errors.AddRange(ValidateDedicatedTask(
            required: Enabled && SessionMode == VoicePeSessionMode.JoydexOwner));

        if (!string.IsNullOrWhiteSpace(DeviceEndpoint)
            && !VoicePeEndpoint.TryParse(DeviceEndpoint, out _))
        {
            errors.Add("The Voice PE endpoint must be an absolute http:// address without credentials, a query, or a fragment.");
        }

        if (Enabled)
        {
            if (string.IsNullOrWhiteSpace(DeviceEndpoint))
            {
                errors.Add("An ESPHome device endpoint is required when the Voice PE bridge is enabled.");
            }

            if (SessionMode == VoicePeSessionMode.JoydexOwner)
            {
                if (string.IsNullOrWhiteSpace(CodexAppServerPath))
                {
                    errors.Add("A Codex App Server executable path is required in Joydex owner mode.");
                }
                else if (!IsFullyQualifiedPath(CodexAppServerPath))
                {
                    errors.Add("The Codex App Server executable path must be fully qualified.");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(CodexAppServerPath)
                 && !IsFullyQualifiedPath(CodexAppServerPath))
        {
            errors.Add("The Codex App Server executable path must be fully qualified.");
        }

        if (!string.IsNullOrWhiteSpace(AgentWorkspacePath)
            && !IsFullyQualifiedPath(AgentWorkspacePath))
        {
            errors.Add("The Voice Agent Workspace path must be fully qualified.");
        }
        if (!string.IsNullOrWhiteSpace(AgentProjectId)
            && string.IsNullOrWhiteSpace(AgentWorkspacePath))
        {
            errors.Add("A Codex project cannot be selected without a Voice Agent Workspace path.");
        }
        if (AgentProjectId.Any(char.IsControl))
        {
            errors.Add("The Codex project id must contain no control characters.");
        }
        if (AgentProjectLabel.Length > MaximumLabelLength || AgentProjectLabel.Any(char.IsControl))
        {
            errors.Add($"The Codex project label must be {MaximumLabelLength} characters or fewer and contain no control characters.");
        }

        if (!string.IsNullOrWhiteSpace(VoiceTargetTaskId)
            && !CodexTaskReference.TryParse(VoiceTargetTaskId, out _))
        {
            errors.Add("The Voice Target must be a Codex task UUID.");
        }
        if (VoiceTargetHostId.Any(char.IsControl) || VoiceTargetHostId.Length > MaximumLabelLength)
        {
            errors.Add($"The Voice Target host id must be {MaximumLabelLength} characters or fewer and contain no control characters.");
        }
        if (VoiceTargetTaskLabel.Any(char.IsControl) || VoiceTargetTaskLabel.Length > MaximumLabelLength)
        {
            errors.Add($"The Voice Target label must be {MaximumLabelLength} characters or fewer and contain no control characters.");
        }
        if (string.IsNullOrWhiteSpace(VoiceTargetTaskId) != string.IsNullOrWhiteSpace(VoiceTargetHostId))
        {
            errors.Add("The Voice Target task id and host id must be selected together.");
        }
        if (DesktopTaskMessagingEnabled && SessionMode != VoicePeSessionMode.JoydexOwner)
        {
            errors.Add("Desktop task messaging requires Joydex owner mode.");
        }

        if (CodexTaskReference.TryParse(PinnedTaskId, out var fallbackTaskId)
            && CodexTaskReference.TryParse(DedicatedTaskId, out var dedicatedTaskId)
            && string.Equals(fallbackTaskId, dedicatedTaskId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("The Dedicated Voice Task must be different from the LASTVOICE fallback launch task.");
        }

        if (!string.IsNullOrWhiteSpace(RealtimeVoice)
            && !SupportedRealtimeVoices.Contains(RealtimeVoice, StringComparer.Ordinal))
        {
            errors.Add("The Realtime voice is not supported by this Joydex build.");
        }

        if (ConversationSpeakerGain is < MinimumConversationSpeakerGain or > MaximumConversationSpeakerGain)
        {
            errors.Add(
                $"The conversation speaker gain must be between {MinimumConversationSpeakerGain}x and "
                + $"{MaximumConversationSpeakerGain}x.");
        }

        return errors;
    }

    public IReadOnlyList<string> ValidatePinnedTask(bool required) =>
        ValidatePinnedTask(required, includeSchemaVersion: true);

    public IReadOnlyList<string> ValidateDedicatedTask(bool required)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(DedicatedTaskId))
        {
            if (required)
            {
                errors.Add("A Dedicated Voice Task is required in Joydex owner mode.");
            }
        }
        else if (!CodexTaskReference.TryParse(DedicatedTaskId, out _))
        {
            errors.Add("The Dedicated Voice Task must be a Codex task UUID or codex://threads/<task-id> deep link.");
        }

        if (DedicatedTaskLabel.Length > MaximumLabelLength || DedicatedTaskLabel.Any(char.IsControl))
        {
            errors.Add($"The Dedicated Voice Task label must be {MaximumLabelLength} characters or fewer and contain no control characters.");
        }

        return errors;
    }

    private IReadOnlyList<string> ValidatePinnedTask(bool required, bool includeSchemaVersion)
    {
        var errors = new List<string>();
        if (includeSchemaVersion && SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"Unsupported Voice PE settings schema version {SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(PinnedTaskId))
        {
            if (required)
            {
                errors.Add("A pinned Codex task is required.");
            }
        }
        else if (!CodexTaskReference.TryParse(PinnedTaskId, out _))
        {
            errors.Add("The pinned task must be a Codex task UUID or codex://threads/<task-id> deep link.");
        }

        if (PinnedTaskLabel.Length > MaximumLabelLength || PinnedTaskLabel.Any(char.IsControl))
        {
            errors.Add($"The pinned task label must be {MaximumLabelLength} characters or fewer and contain no control characters.");
        }

        return errors;
    }

    private static string NormalizeAppServerPath(string value) => NormalizeFileSystemPath(value);

    private static string NormalizeFileSystemPath(string value)
    {
        var candidate = value.Trim();
        if (!IsFullyQualifiedPath(candidate))
        {
            return candidate;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return candidate;
        }
    }

    private static bool IsFullyQualifiedPath(string value)
    {
        try
        {
            return Path.IsPathFullyQualified(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public static class CodexTaskReference
{
    public static bool TryParse(string? value, out string taskId)
    {
        taskId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (Guid.TryParseExact(candidate, "D", out var directId))
        {
            taskId = directId.ToString("D");
            return true;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "codex", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "threads", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (path.Length == 0 || path.Contains('/'))
        {
            return false;
        }

        try
        {
            path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!Guid.TryParseExact(path, "D", out var linkedId))
        {
            return false;
        }

        taskId = linkedId.ToString("D");
        return true;
    }

    public static string BuildDeepLink(string taskId)
    {
        if (!TryParse(taskId, out var normalized))
        {
            throw new ArgumentException("A valid Codex task UUID is required.", nameof(taskId));
        }

        return $"codex://threads/{normalized}";
    }
}

public static class VoicePeEndpoint
{
    public static bool TryParse(string? value, out Uri endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var candidate)
            || !string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(candidate.Host)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || candidate.AbsolutePath is not ("" or "/")
            || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || candidate.Port is <= 0 or > 65_535)
        {
            return false;
        }

        endpoint = new Uri(candidate.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
        return true;
    }
}
