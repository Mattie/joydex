using System.Text;
using System.Text.Json;
using Joydex.Windows.Voice;

namespace Joydex.App;

/// <summary>
/// Persists one readable Room Voice conversation beside the Voice Agent Workspace. Failures are
/// diagnostic-only and never terminate the live media path.
/// </summary>
internal sealed class VoiceSessionArchive
{
    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly Action<string> _log;
    private readonly SessionMetadata _metadata;
    private readonly string? _directory;
    private readonly string? _transcriptPath;
    private string _pendingUser = string.Empty;
    private string _pendingAssistant = string.Empty;
    private string? _lastTranscriptKey;
    private int _completed;

    private VoiceSessionArchive(
        string workspacePath,
        string taskId,
        string projectId,
        string projectLabel,
        Action<string> log,
        DateTimeOffset startedAt,
        string sessionId)
    {
        _log = log;
        _metadata = new SessionMetadata(
            SchemaVersion: 1,
            SessionId: sessionId,
            TaskId: taskId,
            ProjectId: projectId,
            ProjectLabel: projectLabel,
            WorkspacePath: Path.GetFullPath(workspacePath),
            StartedAt: startedAt,
            ConnectedAt: null,
            EndedAt: null,
            Outcome: "starting",
            Reason: null);

        try
        {
            var dayDirectory = Path.Combine(
                GetSessionsRoot(workspacePath),
                startedAt.ToString("yyyy-MM-dd"));
            _directory = Path.Combine(
                dayDirectory,
                $"{startedAt:HHmmss-fff}-{sessionId[..8]}");
            Directory.CreateDirectory(_directory);
            _transcriptPath = Path.Combine(_directory, "transcript.md");
            File.WriteAllText(
                _transcriptPath,
                BuildTranscriptHeader(_metadata),
                Utf8);
            WriteMetadata();
            _log($"Joydex opened Voice Session records at {_directory}.");
        }
        catch (Exception exception) when (IsArchiveException(exception))
        {
            _directory = null;
            _transcriptPath = null;
            _log($"Could not create Joydex Voice Session records: {exception.Message}");
        }
    }

    public string? AudioDirectory => _directory is null
        ? null
        : Path.Combine(_directory, "audio");

    public string SessionId => _metadata.SessionId;

    public static VoiceSessionArchive Create(
        string workspacePath,
        string taskId,
        string projectId,
        string projectLabel,
        Action<string> log,
        DateTimeOffset? startedAt = null,
        string? sessionId = null) => new(
            workspacePath,
            taskId,
            projectId,
            projectLabel,
            log,
            startedAt ?? DateTimeOffset.Now,
            sessionId ?? Guid.NewGuid().ToString("N"));

    public static string GetSessionsRoot(string workspacePath) => Path.Combine(
        Path.GetFullPath(workspacePath),
        ".joydex",
        "voice-sessions");

    public void MarkConnected()
    {
        lock (_gate)
        {
            if (_completed != 0)
            {
                return;
            }

            _metadata.ConnectedAt = DateTimeOffset.Now;
            _metadata.Outcome = "active";
            TryWriteMetadata();
        }
    }

    public void UpdateTranscript(CodexVoiceConversationKind kind, string text, bool final)
    {
        if (kind == CodexVoiceConversationKind.Activity)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed != 0)
            {
                return;
            }

            ref var pending = ref (kind == CodexVoiceConversationKind.User
                ? ref _pendingUser
                : ref _pendingAssistant);
            var normalized = text.Trim();
            if (!final)
            {
                if (normalized.Length > 0)
                {
                    pending = RoomVoiceConversationModel.MergeTranscript(pending, normalized);
                }
                return;
            }

            var completed = normalized.Length > 0 ? normalized : pending;
            pending = string.Empty;
            if (completed.Length == 0 || _transcriptPath is null)
            {
                return;
            }

            var key = $"{kind}:{completed}";
            if (string.Equals(_lastTranscriptKey, key, StringComparison.Ordinal))
            {
                return;
            }
            _lastTranscriptKey = key;

            try
            {
                File.AppendAllText(
                    _transcriptPath,
                    BuildTranscriptEntry(kind, completed, DateTimeOffset.Now),
                    Utf8);
            }
            catch (Exception exception) when (IsArchiveException(exception))
            {
                _log($"Could not append the Joydex Voice Session transcript: {exception.Message}");
            }
        }
    }

    public void Complete(string outcome, string? reason = null)
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            _metadata.EndedAt = DateTimeOffset.Now;
            _metadata.Outcome = string.IsNullOrWhiteSpace(outcome) ? "ended" : outcome.Trim();
            _metadata.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            TryWriteMetadata();
        }
    }

    private static string BuildTranscriptHeader(SessionMetadata metadata)
    {
        var builder = new StringBuilder()
            .AppendLine("# Joydex Voice Session")
            .AppendLine()
            .Append("- Started: ").AppendLine(metadata.StartedAt.ToString("O"))
            .Append("- Task: ").AppendLine(metadata.TaskId)
            .Append("- Workspace: ").AppendLine(metadata.WorkspacePath);
        if (metadata.ProjectLabel.Length > 0)
        {
            builder.Append("- Codex project: ").AppendLine(metadata.ProjectLabel);
        }

        return builder
            .AppendLine()
            .AppendLine("## Conversation")
            .AppendLine()
            .ToString();
    }

    private static string BuildTranscriptEntry(
        CodexVoiceConversationKind kind,
        string text,
        DateTimeOffset timestamp)
    {
        var role = kind == CodexVoiceConversationKind.User ? "You" : "Computer";
        var quoted = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\n> ", StringComparison.Ordinal);
        return $"### {role} — {timestamp:t}{Environment.NewLine}{Environment.NewLine}> {quoted}"
            + Environment.NewLine + Environment.NewLine;
    }

    private void TryWriteMetadata()
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            WriteMetadata();
        }
        catch (Exception exception) when (IsArchiveException(exception))
        {
            _log($"Could not update Joydex Voice Session metadata: {exception.Message}");
        }
    }

    private void WriteMetadata()
    {
        if (_directory is null)
        {
            return;
        }

        var path = Path.Combine(_directory, "session.json");
        var temporaryPath = Path.Combine(_directory, $".session.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, _metadata, JsonOptions);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsArchiveException(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or ArgumentException
        or NotSupportedException
        or PathTooLongException;

    private sealed class SessionMetadata(
        int SchemaVersion,
        string SessionId,
        string TaskId,
        string ProjectId,
        string ProjectLabel,
        string WorkspacePath,
        DateTimeOffset StartedAt,
        DateTimeOffset? ConnectedAt,
        DateTimeOffset? EndedAt,
        string Outcome,
        string? Reason)
    {
        public int SchemaVersion { get; } = SchemaVersion;
        public string SessionId { get; } = SessionId;
        public string TaskId { get; } = TaskId;
        public string ProjectId { get; } = ProjectId;
        public string ProjectLabel { get; } = ProjectLabel;
        public string WorkspacePath { get; } = WorkspacePath;
        public DateTimeOffset StartedAt { get; } = StartedAt;
        public DateTimeOffset? ConnectedAt { get; set; } = ConnectedAt;
        public DateTimeOffset? EndedAt { get; set; } = EndedAt;
        public string Outcome { get; set; } = Outcome;
        public string? Reason { get; set; } = Reason;
    }
}
