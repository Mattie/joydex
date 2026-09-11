using System.IO.Pipes;
using System.Text.Json;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

public interface IDesktopTaskBridgeClient
{
    Task<bool> IsAvailableAsync(string sourceThreadId, CancellationToken cancellationToken = default);

    Task<DesktopTaskCatalog> ListTasksAsync(
        string sourceThreadId,
        string? excludedThreadId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one saved local task directly, independent of the bounded recent-task catalog.
    /// </summary>
    Task<DesktopTaskSummary> ResolveTaskAsync(
        string sourceThreadId,
        string targetThreadId,
        string targetHostId,
        CancellationToken cancellationToken = default);

    Task<string> ReadTaskAsync(
        string sourceThreadId,
        DesktopTaskSummary target,
        CancellationToken cancellationToken = default);

    Task<DesktopTaskDeliveryResult> SendMessageAsync(
        string sourceThreadId,
        DesktopTaskSummary target,
        string message,
        CancellationToken cancellationToken = default);
}

public sealed class DesktopTaskBridgeClient(
    string pipeName = DesktopTaskBridgeProtocol.PipeName) : IDesktopTaskBridgeClient
{
    public const int MaximumMessageLength = DesktopTaskBridgeClientLimits.MaximumMessageLength;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName = string.IsNullOrWhiteSpace(pipeName)
        ? throw new ArgumentException("A Desktop task bridge pipe name is required.", nameof(pipeName))
        : pipeName.Trim();

    public async Task<bool> IsAvailableAsync(
        string sourceThreadId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await RequestAsync(
                    DesktopTaskBridgeProtocol.StatusMethod,
                    sourceThreadId,
                    new { },
                    TimeSpan.FromSeconds(3),
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or TimeoutException
            or InvalidDataException
            or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<DesktopTaskCatalog> ListTasksAsync(
        string sourceThreadId,
        string? excludedThreadId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
                DesktopTaskBridgeProtocol.ListTasksMethod,
                sourceThreadId,
                new { limit = 50 },
                TimeSpan.FromSeconds(15),
                cancellationToken)
            .ConfigureAwait(false);
        var content = ReadContent(result);
        using var document = JsonDocument.Parse(content);
        var tasks = new List<DesktopTaskSummary>();
        AddTasks(document.RootElement, "pinnedThreads", pinned: true, tasks);
        AddTasks(document.RootElement, "threads", pinned: false, tasks);
        var normalizedExcluded = NormalizeTaskId(excludedThreadId);
        var filtered = tasks
            .Where(task => task.HostId.Equals("local", StringComparison.OrdinalIgnoreCase))
            .Where(task => !task.Status.Equals("archived", StringComparison.OrdinalIgnoreCase))
            .Where(task => !string.Equals(task.Id, normalizedExcluded, StringComparison.OrdinalIgnoreCase))
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(task => task.Pinned).First())
            .OrderByDescending(task => IsRunning(task.Status))
            .ThenByDescending(task => task.Pinned)
            .ThenByDescending(task => task.UpdatedAt)
            .ToArray();
        return new DesktopTaskCatalog(filtered);
    }

    public async Task<DesktopTaskSummary> ResolveTaskAsync(
        string sourceThreadId,
        string targetThreadId,
        string targetHostId,
        CancellationToken cancellationToken = default)
    {
        if (!CodexTaskReference.TryParse(targetThreadId, out var normalizedTargetThreadId))
        {
            throw new ArgumentException("A valid target task ID is required.", nameof(targetThreadId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHostId);
        var normalizedTargetHostId = targetHostId.Trim();
        var result = await RequestAsync(
                DesktopTaskBridgeProtocol.ReadTaskMethod,
                sourceThreadId,
                new
                {
                    threadId = normalizedTargetThreadId,
                    hostId = normalizedTargetHostId,
                    turnLimit = 1,
                    maxOutputCharsPerItem = 1,
                },
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
        return ReadTaskSummary(
            ReadContent(result),
            normalizedTargetThreadId,
            normalizedTargetHostId);
    }

    public async Task<string> ReadTaskAsync(
        string sourceThreadId,
        DesktopTaskSummary target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var result = await RequestAsync(
                DesktopTaskBridgeProtocol.ReadTaskMethod,
                sourceThreadId,
                new
                {
                    threadId = target.Id,
                    hostId = target.HostId,
                    turnLimit = 10,
                },
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
        return ReadContent(result);
    }

    public async Task<DesktopTaskDeliveryResult> SendMessageAsync(
        string sourceThreadId,
        DesktopTaskSummary target,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > MaximumMessageLength)
        {
            throw new InvalidDataException(
                $"A Desktop task message cannot exceed {MaximumMessageLength} characters.");
        }

        var result = await RequestAsync(
                DesktopTaskBridgeProtocol.SendMessageMethod,
                sourceThreadId,
                new
                {
                    threadId = target.Id,
                    hostId = target.HostId,
                    prompt = message,
                },
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
        var detail = ReadContent(result);
        return new DesktopTaskDeliveryResult(
            target.Id,
            target.HostId,
            target.Title,
            Queued: IsRunning(target.Status),
            detail);
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        string sourceThreadId,
        object arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (method != DesktopTaskBridgeProtocol.StatusMethod
            && !CodexTaskReference.TryParse(sourceThreadId, out sourceThreadId))
        {
            throw new ArgumentException("A valid source task ID is required.", nameof(sourceThreadId));
        }

        var requestId = Guid.NewGuid().ToString("N");
        var request = new DesktopTaskBridgeRequest(
            DesktopTaskBridgeProtocol.Version,
            requestId,
            method,
            sourceThreadId,
            JsonSerializer.SerializeToElement(arguments, JsonOptions));
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await DesktopTaskBridgeFraming.WriteAsync(
                    pipe,
                    payload,
                    DesktopTaskBridgeProtocol.MaximumFrameBytes,
                    deadline.Token)
                .ConfigureAwait(false);
            var responseBytes = await DesktopTaskBridgeFraming.ReadAsync(
                    pipe,
                    DesktopTaskBridgeProtocol.MaximumFrameBytes,
                    deadline.Token)
                .ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<DesktopTaskBridgeResponse>(responseBytes, JsonOptions)
                ?? throw new InvalidDataException("Desktop task bridge returned an empty response.");
            if (response.Version != DesktopTaskBridgeProtocol.Version
                || !string.Equals(response.Id, requestId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Desktop task bridge response did not match its request.");
            }
            if (!response.Success)
            {
                throw new InvalidOperationException(
                    response.Error ?? "Desktop task bridge rejected the request.");
            }
            return response.Result.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Desktop task bridge {method} timed out after {timeout.TotalSeconds:0} seconds.");
        }
    }

    private static string ReadContent(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("content", out var content)
            && content.GetString() is { } text)
        {
            return text;
        }
        throw new InvalidDataException("Desktop task bridge returned no content.");
    }

    private static DesktopTaskSummary ReadTaskSummary(
        string content,
        string expectedThreadId,
        string expectedHostId)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (!root.TryGetProperty("thread", out var thread)
            || thread.ValueKind != JsonValueKind.Object
            || !thread.TryGetProperty("kind", out var kind)
            || !string.Equals(kind.GetString(), "codex", StringComparison.OrdinalIgnoreCase)
            || !thread.TryGetProperty("id", out var idElement)
            || !CodexTaskReference.TryParse(idElement.GetString(), out var id)
            || !id.Equals(expectedThreadId, StringComparison.OrdinalIgnoreCase)
            || !thread.TryGetProperty("hostId", out var hostElement)
            || hostElement.GetString() is not { Length: > 0 } hostId
            || !hostId.Equals(expectedHostId, StringComparison.OrdinalIgnoreCase)
            || !hostId.Equals("local", StringComparison.OrdinalIgnoreCase)
            || !thread.TryGetProperty("title", out var titleElement)
            || titleElement.GetString() is not { Length: > 0 } title)
        {
            throw new InvalidDataException("Desktop task bridge returned a different or invalid task.");
        }
        var status = ReadTaskStatus(thread);
        if (string.IsNullOrWhiteSpace(status))
        {
            throw new InvalidDataException("Desktop task bridge returned a task without a status.");
        }
        if (status.Equals("archived", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected Desktop task is archived.");
        }
        return new DesktopTaskSummary(
            id,
            hostId,
            title,
            status,
            thread.TryGetProperty("projectId", out var project) ? project.GetString() : null,
            thread.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null,
            thread.TryGetProperty("updatedAt", out var updated) && updated.TryGetInt64(out var timestamp)
                ? timestamp
                : 0);
    }

    private static string ReadTaskStatus(JsonElement thread)
    {
        if (!thread.TryGetProperty("status", out var status)) return string.Empty;
        if (status.ValueKind == JsonValueKind.String) return status.GetString() ?? string.Empty;
        if (status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String)
        {
            return type.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static void AddTasks(
        JsonElement root,
        string property,
        bool pinned,
        ICollection<DesktopTaskSummary> destination)
    {
        if (!root.TryGetProperty(property, out var tasks) || tasks.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var task in tasks.EnumerateArray())
        {
            if (!task.TryGetProperty("kind", out var kind)
                || !string.Equals(kind.GetString(), "codex", StringComparison.OrdinalIgnoreCase)
                || !task.TryGetProperty("id", out var idElement)
                || !CodexTaskReference.TryParse(idElement.GetString(), out var id)
                || !task.TryGetProperty("hostId", out var hostElement)
                || hostElement.GetString() is not { Length: > 0 } hostId
                || !task.TryGetProperty("title", out var titleElement)
                || titleElement.GetString() is not { Length: > 0 } title)
            {
                continue;
            }

            destination.Add(new DesktopTaskSummary(
                id,
                hostId,
                title,
                task.TryGetProperty("status", out var status) ? status.GetString() ?? string.Empty : string.Empty,
                task.TryGetProperty("projectId", out var project) ? project.GetString() : null,
                task.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null,
                task.TryGetProperty("updatedAt", out var updated) && updated.TryGetInt64(out var timestamp)
                    ? timestamp
                    : 0,
                pinned));
        }
    }

    private static string? NormalizeTaskId(string? value) =>
        CodexTaskReference.TryParse(value, out var id) ? id : null;

    private static bool IsRunning(string status) =>
        status.Equals("active", StringComparison.OrdinalIgnoreCase)
        || status.Equals("running", StringComparison.OrdinalIgnoreCase)
        || status.Equals("inProgress", StringComparison.OrdinalIgnoreCase);
}
