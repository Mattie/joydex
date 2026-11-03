namespace Joydex.Core.T3;

/// <summary>Metadata projected on the environment host. Conversation bodies never cross SSH.</summary>
public sealed record T3ReadResult
{
    public bool Success { get; init; }
    public string? ErrorClass { get; init; }
    public string? Reason { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? TransportKind { get; init; }
    public bool? Enabled { get; init; }
    public string SourceEpoch { get; init; } = "";
    public long Sequence { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public bool IsComplete { get; init; }
    public T3TaskMetadata[] Tasks { get; init; } = [];
    public string? BindingFingerprint { get; init; }
    public string ContextDiagnostic { get; init; } = "Active Electron environment and keymap are not verified.";
}

public sealed record T3TaskMetadata
{
    public string Id { get; init; } = "";
    public string? RunId { get; init; }
    public string Status { get; init; } = "unknown";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? LatestUserAuthoredMessageAt { get; init; }
    public bool ReadComplete { get; init; }
    public bool RequestsComplete { get; init; }
    public T3PendingRequest[] PendingRequests { get; init; } = [];
    public T3PendingRequest[] ResolvedQuestions { get; init; } = [];
    public string? Title { get; init; }
    public string? Project { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
}

public sealed record T3PendingRequest
{
    public string Id { get; init; } = "";
    public string? RunId { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public bool Blocking { get; init; } = true;
}

public sealed record T3ShortcutSetupResult
{
    public bool Success { get; init; }
    public string? ErrorClass { get; init; }
    public string? Reason { get; init; }
    public string? BindingFingerprint { get; init; }
    public string[] AddedCommands { get; init; } = [];
    public string[] RemovedCommands { get; init; } = [];
    public string[] Diagnostics { get; init; } = [];
    public T3ShortcutRule[] Rules { get; init; } = [];
    public string? CapabilitySource { get; init; }
    public string ContextDiagnostic { get; init; } = "Active Electron environment and keymap are not verified.";
}

public sealed record T3ShortcutRule(string Command, string Key, string? When = null);
