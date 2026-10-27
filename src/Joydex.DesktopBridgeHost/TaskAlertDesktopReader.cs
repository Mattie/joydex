using System.Text.Json;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

/// <summary>Projects Desktop metadata to alert state without returning conversation content.</summary>
internal static class TaskAlertDesktopReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static async Task<TaskAlertDesktopSnapshot> ReadAsync(
        IReadOnlyList<string> knownTaskIds,
        Func<string, JsonElement, CancellationToken, Task<string>> call,
        CancellationToken cancellationToken)
    {
        if (knownTaskIds.Count > TaskAlertSlots.All.Length || knownTaskIds.Any(id => !Guid.TryParse(id, out _)))
            throw new InvalidDataException("Invalid task-alert scan selection.");
        var candidates = new HashSet<string>(knownTaskIds, StringComparer.OrdinalIgnoreCase);
        using (var catalog = JsonDocument.Parse(await call("list_threads",
                   JsonSerializer.SerializeToElement(new { limit = 50 }), cancellationToken).ConfigureAwait(false)))
        {
            foreach (var property in new[] { "pinnedThreads", "threads" })
            {
                if (!catalog.RootElement.TryGetProperty(property, out var entries) || entries.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var thread in entries.EnumerateArray())
                {
                    if (IsLocalTask(thread) && IsActive(Status(thread)) && Text(thread, "id") is { } id
                        && Guid.TryParse(id, out _) && candidates.Count < 60)
                        candidates.Add(id);
                }
            }
        }
        var observations = new List<TaskAlertDesktopObservation>();
        var unavailable = 0;
        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var content = await call("read_thread", JsonSerializer.SerializeToElement(new
                {
                    threadId = id, hostId = "local", turnLimit = 1,
                    includeOutputs = false, maxOutputCharsPerItem = 1,
                }), cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(content);
                var observation = Parse(document.RootElement, id);
                if (observation is not null) observations.Add(observation);
                else unavailable++;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException)
            {
                unavailable++;
            }
        }
        var snapshot = new TaskAlertDesktopSnapshot(observations.ToArray(), unavailable);
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length > DesktopTaskBridgeProtocol.MaximumFrameBytes - 8192)
            snapshot = snapshot with { Tasks = snapshot.Tasks.Select(item => item with { Workspace = null }).ToArray() };
        return snapshot;
    }

    internal static TaskAlertDesktopObservation? Parse(JsonElement root, string expectedId)
    {
        if (!root.TryGetProperty("thread", out var thread) || !IsLocalTask(thread)
            || !string.Equals(Text(thread, "id"), expectedId, StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("page", out var page) || Text(page, "order") != "newest_first"
            || !root.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array
            || turns.GetArrayLength() != 1) return null;
        var turn = turns[0];
        var turnId = Text(turn, "id");
        var started = Timestamp(turn, "startedAt");
        if (string.IsNullOrWhiteSpace(turnId) || turnId.Length > 512 || started is null) return null;
        var turnStatus = Text(turn, "status");
        var active = IsActive(Status(thread));
        TaskAlertState state;
        DateTimeOffset? completed = null;
        if (active && turnStatus == "inProgress")
        {
            state = TaskAlertState.Running;
            // Generic waiting flags cannot identify an individual question or its dismissal.
            // Attention remains owned by correlated hooks and explicit user actions.
        }
        else if (Status(thread) is "idle" or "notLoaded" && turnStatus is "completed" or "failed" or "interrupted")
        {
            completed = Timestamp(turn, "completedAt");
            if (completed is null || completed < started) return null;
            state = turnStatus == "completed" ? TaskAlertState.Completed : TaskAlertState.Fault;
        }
        else return null;
        var workspace = TaskAlertSuppression.NormalizeWorkspace(Text(thread, "cwd"));
        return new(expectedId, turnId, state, started.Value, completed, workspace,
            TaskAlertSuppression.CreateWorkspaceKey(workspace));
    }

    private static bool IsLocalTask(JsonElement thread) => Text(thread, "kind") == "codex"
        && Text(thread, "hostId") == "local";

    private static bool IsActive(string? value) => value is "active" or "running" or "inProgress";

    private static string? Status(JsonElement thread) => !thread.TryGetProperty("status", out var status) ? null
        : status.ValueKind == JsonValueKind.String ? status.GetString() : Text(status, "type");

    private static string? Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    private static DateTimeOffset? Timestamp(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var timestamp) || timestamp.ValueKind != JsonValueKind.Number
            || !timestamp.TryGetInt64(out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
