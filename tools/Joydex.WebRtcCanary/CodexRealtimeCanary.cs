using System.Text.Json;

namespace Joydex.WebRtcCanary;

internal sealed class CodexRealtimeCanary
{
    private readonly CodexAppServerClient _client;
    private readonly CanaryState _state;
    private readonly string? _configuredThreadId;
    private readonly string _configuredThreadTitle;
    private readonly bool _useEphemeralThread;
    private readonly object _sessionGate = new();
    private TaskCompletionSource<string>? _sdpAnswer;
    private TaskCompletionSource<bool>? _closed;
    private string? _activeThreadId;
    private bool _audioCanarySent;

    public CodexRealtimeCanary(
        CodexAppServerClient client,
        CanaryState state,
        string? configuredThreadId,
        string configuredThreadTitle,
        bool useEphemeralThread)
    {
        _client = client;
        _state = state;
        _configuredThreadId = configuredThreadId;
        _configuredThreadTitle = configuredThreadTitle;
        _useEphemeralThread = useEphemeralThread;
        _client.NotificationReceived += OnNotification;
    }

    public async Task<string> StartAsync(string sdpOffer, CancellationToken cancellationToken)
    {
        _state.StartingRealtime();
        var (threadId, needsResume) = await ResolveThreadAsync(cancellationToken).ConfigureAwait(false);
        if (needsResume)
        {
            _state.ResumingThread();
            await _client.RequestAsync(
                "thread/resume",
                new
                {
                    threadId,
                    excludeTurns = true,
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
        }

        TaskCompletionSource<string> answer;

        lock (_sessionGate)
        {
            if (_activeThreadId is not null)
            {
                throw new InvalidOperationException("The canary already has an active Realtime session.");
            }

            _activeThreadId = threadId;
            _audioCanarySent = false;
            answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sdpAnswer = answer;
            _closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            await _client.RequestAsync(
                "thread/realtime/start",
                new
                {
                    threadId,
                    realtimeSessionId = $"joydex-canary-{Guid.NewGuid():N}",
                    version = "v3",
                    outputModality = "audio",
                    includeStartupContext = false,
                    transport = new
                    {
                        type = "webrtc",
                        sdp = sdpOffer,
                    },
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            return await answer.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sessionGate)
            {
                _activeThreadId = null;
            }

            throw;
        }
    }

    public void MediaConnected()
    {
        lock (_sessionGate)
        {
            _ = _activeThreadId
                ?? throw new InvalidOperationException("No Realtime session is active.");
        }

        _state.MediaConnected();
    }

    public async Task SendAudioCanaryAsync(CancellationToken cancellationToken)
    {
        string threadId;
        lock (_sessionGate)
        {
            threadId = _activeThreadId
                ?? throw new InvalidOperationException("No Realtime session is active.");
            if (_audioCanarySent)
            {
                return;
            }

            _audioCanarySent = true;
        }

        await _client.RequestAsync(
            "thread/realtime/appendText",
            new
            {
                threadId,
                role = "user",
                text = "Please briefly say exactly: Joydex WebRTC canary connected.",
            },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
    }

    public void AudioReceived() => _state.AudioReceived();

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        string? threadId;
        TaskCompletionSource<bool>? closed;
        lock (_sessionGate)
        {
            threadId = _activeThreadId;
            closed = _closed;
        }

        if (threadId is null)
        {
            return;
        }

        await _client.RequestAsync(
            "thread/realtime/stop",
            new { threadId },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        if (closed is not null)
        {
            try
            {
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _state.Closed("explicit stop completed; no closed notification arrived within 10 seconds");
            }
        }
    }

    private async Task<(string ThreadId, bool NeedsResume)> ResolveThreadAsync(
        CancellationToken cancellationToken)
    {
        if (_useEphemeralThread)
        {
            _state.ResolvingThread(_configuredThreadTitle);
            var startResult = await _client.RequestAsync(
                "thread/start",
                new
                {
                    ephemeral = true,
                    approvalPolicy = "never",
                    sandbox = "read-only",
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (!startResult.TryGetProperty("thread", out var thread) ||
                !thread.TryGetProperty("id", out var idElement) ||
                idElement.GetString() is not { Length: > 0 } id)
            {
                throw new InvalidOperationException("thread/start returned no ephemeral task id.");
            }

            _state.ThreadResolved(_configuredThreadTitle, id);
            return (id, false);
        }

        if (!string.IsNullOrWhiteSpace(_configuredThreadId))
        {
            _state.ThreadResolved(_configuredThreadTitle, _configuredThreadId);
            return (_configuredThreadId, true);
        }

        _state.ResolvingThread(_configuredThreadTitle);
        var result = await _client.RequestAsync(
            "thread/list",
            new
            {
                searchTerm = _configuredThreadTitle,
                archived = false,
                limit = 100,
            },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("thread/list returned no task data.");
        }

        var matches = data.EnumerateArray()
            .Select(thread => new
            {
                Id = thread.GetProperty("id").GetString(),
                Name = thread.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null,
                Preview = thread.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.String
                    ? preview.GetString()
                    : null,
                Recency = thread.TryGetProperty("recencyAt", out var recency) && recency.TryGetInt64(out var value)
                    ? value
                    : 0,
            })
            .Where(thread =>
                string.Equals(thread.Name, _configuredThreadTitle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(thread.Preview, _configuredThreadTitle, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(thread => thread.Recency)
            .ToArray();

        if (matches.Length == 0 || string.IsNullOrWhiteSpace(matches[0].Id))
        {
            throw new InvalidOperationException(
                $"Could not find an active task named '{_configuredThreadTitle}'. Pass --thread-id to target it explicitly.");
        }

        var selected = matches[0];
        _state.ThreadResolved(selected.Name ?? _configuredThreadTitle, selected.Id!);
        return (selected.Id!, true);
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        var notificationThreadId = parameters.ValueKind == JsonValueKind.Object &&
                                   parameters.TryGetProperty("threadId", out var threadIdElement)
            ? threadIdElement.GetString()
            : null;

        lock (_sessionGate)
        {
            if (_activeThreadId is null ||
                !string.Equals(_activeThreadId, notificationThreadId, StringComparison.Ordinal))
            {
                return;
            }
        }

        switch (method)
        {
            case "thread/realtime/started":
                _state.RealtimeStarted();
                break;

            case "thread/realtime/sdp":
                if (parameters.TryGetProperty("sdp", out var sdpElement) &&
                    sdpElement.GetString() is { Length: > 0 } sdp)
                {
                    _state.SdpReceived();
                    _sdpAnswer?.TrySetResult(sdp);
                }

                break;

            case "thread/realtime/outputAudio/delta":
                _state.AudioReceived();
                break;

            case "thread/realtime/error":
                var message = parameters.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString() ?? "Unknown Realtime error."
                    : "Unknown Realtime error.";
                _state.Failed(message);
                _sdpAnswer?.TrySetException(new InvalidOperationException(message));
                break;

            case "thread/realtime/closed":
                var reason = parameters.TryGetProperty("reason", out var reasonElement) &&
                             reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;
                _state.Closed(reason);
                lock (_sessionGate)
                {
                    _activeThreadId = null;
                    _closed?.TrySetResult(true);
                }

                break;
        }
    }
}
