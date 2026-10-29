namespace Joydex.Core.TaskAlerts;

/// <summary>A content-free observation of a saved local Desktop task's latest turn.</summary>
public sealed record TaskAlertDesktopObservation(
    string SessionId, string TurnId, TaskAlertState State, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, string? Workspace = null, string? WorkspaceKey = null);

/// <summary>Positive observations only; absence from this bounded scan never ends a task.</summary>
public sealed record TaskAlertDesktopSnapshot(TaskAlertDesktopObservation[] Tasks, int UnavailableCount = 0);
