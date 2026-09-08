using System.Net.Http.Headers;
using Joydex.Windows.WirelessPanel;

namespace Joydex.Windows.Voice;

public enum VoicePeSessionState
{
    Armed,
    Starting,
    Listening,
    Muted,
    Error,
}

public enum VoicePeControlSignal
{
    Wake,
    Hangup,
    ToggleMute,
}

public interface IVoicePeControlTransport : IAsyncDisposable
{
    Task RunAsync(
        Func<VoicePeControlSignal, CancellationToken, ValueTask> onSignal,
        CancellationToken cancellationToken = default);

    Task SetSessionStateAsync(
        VoicePeSessionState state,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Maintains an outbound ESPHome Web Server event stream for the dedicated Room Voice device.
/// </summary>
public sealed class EspHomeVoicePeTransport : IVoicePeControlTransport
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    // ESPHome emits a Web Server SSE ping every ten seconds. Treating three or
    // four missing pings as healthy left wake detection blind for most of a
    // minute after a half-open LAN connection. Fifteen seconds preserves one
    // ping of jitter while bounding recovery from a stale stream.
    private static readonly TimeSpan SseIdleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StateCommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StateCommandRetryDelay = TimeSpan.FromMilliseconds(150);
    private const int StateCommandAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsHttpClient;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _lifecycleGate = new();
    private Task? _runTask;
    private bool _disposed;

    public EspHomeVoicePeTransport(Uri baseUri, Action<string>? log = null)
        : this(new HttpClient(), baseUri, ownsHttpClient: true, log)
    {
    }

    internal EspHomeVoicePeTransport(
        HttpClient httpClient,
        Uri baseUri,
        Action<string>? log = null)
        : this(httpClient, baseUri, ownsHttpClient: false, log)
    {
    }

    private EspHomeVoicePeTransport(
        HttpClient httpClient,
        Uri baseUri,
        bool ownsHttpClient,
        Action<string>? log)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        _ownsHttpClient = ownsHttpClient;
        _log = log;
    }

    public Task RunAsync(
        Func<VoicePeControlSignal, CancellationToken, ValueTask> onSignal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSignal);
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("The Voice PE event loop is already running.");
            }

            _runTask = RunCoreAsync(onSignal, cancellationToken);
            return _runTask;
        }
    }

    public async Task SetSessionStateAsync(
        VoicePeSessionState state,
        CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var option = Uri.EscapeDataString(state.ToString());
        for (var attempt = 1; attempt <= StateCommandAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token);
            timeout.CancelAfter(StateCommandTimeout);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(_baseUri, $"select/joydex_voice_session_state/set?option={option}"));
            try
            {
                using var response = await _httpClient
                    .SendAsync(request, timeout.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception exception) when (
                attempt < StateCommandAttempts
                && exception is HttpRequestException or OperationCanceledException
                && !cancellationToken.IsCancellationRequested
                && !_disposeCancellation.IsCancellationRequested)
            {
                _log?.Invoke(
                    $"Voice PE session state {state} attempt {attempt} failed; retrying: {exception.Message}");
                await Task.Delay(StateCommandRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? runTask;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCancellation.Cancel();
            runTask = _runTask;
        }

        try
        {
            if (runTask is not null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }

            _disposeCancellation.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private async Task RunCoreAsync(
        Func<VoicePeControlSignal, CancellationToken, ValueTask> onSignal,
        CancellationToken callerCancellation)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            _disposeCancellation.Token);
        var cancellationToken = linkedCancellation.Token;
        var failureReported = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReadOneConnectionAsync(onSignal, cancellationToken).ConfigureAwait(false);
                ReportFailure("Voice PE event stream ended; reconnecting.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is
                HttpRequestException or
                IOException or
                TimeoutException or
                InvalidDataException)
            {
                ReportFailure($"Voice PE connection failed: {exception.Message}");
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        void ReportFailure(string message)
        {
            if (!failureReported)
            {
                _log?.Invoke(message);
                failureReported = true;
            }
        }
    }

    private async Task ReadOneConnectionAsync(
        Func<VoicePeControlSignal, CancellationToken, ValueTask> onSignal,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "events"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("Voice PE /events did not return an event stream.");
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var tracker = new VoicePeControlSignalTracker();
        await EspHomeSseParser.ReadAsync(
                stream,
                async (sseEvent, token) =>
                {
                    if (string.Equals(sseEvent.EventType, "state", StringComparison.Ordinal)
                        && EspHomeStateEventParser.TryParse(sseEvent.Data, out var stateEvent)
                        && tracker.TryObserve(stateEvent, out var signal))
                    {
                        await onSignal(signal, token).ConfigureAwait(false);
                    }
                },
                SseIdleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class VoicePeControlSignalTracker
{
    private static readonly IReadOnlyDictionary<string, VoicePeControlSignal> SignalIdentifiers =
        new Dictionary<string, VoicePeControlSignal>(StringComparer.Ordinal)
    {
        ["binary_sensor/Joydex Voice Wake"] = VoicePeControlSignal.Wake,
        ["binary_sensor-joydex_voice_wake"] = VoicePeControlSignal.Wake,
        ["binary_sensor/Joydex Voice Hangup"] = VoicePeControlSignal.Hangup,
        ["binary_sensor-joydex_voice_hangup"] = VoicePeControlSignal.Hangup,
        ["binary_sensor/Joydex Voice Toggle Mute"] = VoicePeControlSignal.ToggleMute,
        ["binary_sensor-joydex_voice_toggle_mute"] = VoicePeControlSignal.ToggleMute,
    };

    private readonly Dictionary<VoicePeControlSignal, bool> _lastStates = [];

    internal VoicePeControlSignalTracker(
        VoicePeControlSignal? initialSignal = null,
        bool initialState = false)
    {
        if (initialSignal.HasValue)
        {
            _lastStates[initialSignal.Value] = initialState;
        }
    }

    public bool TryObserve(EspHomeStateEvent stateEvent, out VoicePeControlSignal signal)
    {
        if (!SignalIdentifiers.TryGetValue(stateEvent.Identifier, out signal))
        {
            return false;
        }

        if (!_lastStates.TryGetValue(signal, out var wasOn))
        {
            _lastStates[signal] = stateEvent.IsOn;
            return false;
        }

        _lastStates[signal] = stateEvent.IsOn;
        return !wasOn && stateEvent.IsOn;
    }
}
