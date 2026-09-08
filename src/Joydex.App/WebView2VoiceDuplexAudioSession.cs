using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Joydex.App;

/// <summary>
/// Hosts one silent Chromium WebRTC peer and exposes its media as raw PCM.
/// </summary>
internal sealed class WebView2VoiceDuplexAudioSession : IVoiceDuplexAudioSession
{
    private static readonly TimeSpan BrowserReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PeerConnectTimeout = TimeSpan.FromSeconds(30);
    private const int ConversationMicrophoneGain = 8;
    private const int MaximumControlTranscriptLength = 480;
    private static readonly VoicePcmFormat MicrophoneFormat = new(16_000, 1);
    private static readonly VoicePcmFormat SpeakerFormat = new(24_000, 1);
    private static readonly int MicrophoneFrameBytes = MicrophoneFormat.GetByteCount(TimeSpan.FromMilliseconds(20));
    private static readonly int SpeakerFrameBytes = SpeakerFormat.GetByteCount(TimeSpan.FromMilliseconds(20));
    private const int SpeakerFramesPerSecond = 50;
    private const int SpeakerSessionDiagnosticSegmentFrames = SpeakerFramesPerSecond * 60 * 5;

    private readonly SynchronizationContext _uiContext;
    private readonly string _userDataDirectory;
    private readonly Func<string, CancellationToken, Task<string>> _negotiate;
    private readonly Action _mediaConnected;
    private readonly Task _realtimeReady;
    private readonly Task _realtimeCompletion;
    private readonly Func<CancellationToken, Task> _stopRealtime;
    private readonly Func<ValueTask> _disposeRealtime;
    private readonly int _conversationSpeakerGain;
    private readonly bool _preserveAssistantAudioDiagnostics;
    private readonly string _diagnosticsDirectory;
    private readonly Action<CodexVoiceConversationKind, string, bool>? _transcriptChanged;
    private readonly Action<string>? _activityChanged;
    private readonly Action<string>? _log;
    // Continuous remote-track capture begins before the device transport opens. The recovering
    // transport's bounded startup can include three eight-second attempts plus cleanup and delay,
    // so retain more than 40 seconds without turning this lossless queue into an unbounded buffer.
    internal const int MaximumBufferedSpeakerOutputs = 4096;

    // The WebView UI callback is the single writer. Keeping audio and lifecycle in one bounded FIFO
    // prevents playback_end/flush from overtaking PCM without permitting an upstream stall to grow
    // memory indefinitely. Exhaustion ends the session visibly instead of dropping accepted speech.
    private readonly Channel<VoiceSpeakerOutput> _speakerOutput =
        CreateSpeakerOutputChannel();
    private readonly TaskCompletionSource<bool> _browserReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _offer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _peerConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _dataChannelOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _remoteTrackReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _browserStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stopGate = new();
    private readonly object _speakerSessionDiagnosticGate = new();
    private readonly MemoryStream _speakerDiagnosticPcm = new();
    private readonly MemoryStream _speakerSessionDiagnosticPcm = new();
    private readonly List<Task> _speakerSessionDiagnosticWrites = [];
    private OffscreenVoiceForm? _form;
    private WebView2? _webView;
    private long _speakerSequence;
    private long _microphoneFramesSent;
    private long _speakerFramesReceived;
    private long _speakerNonSilentFramesReceived;
    private int _speakerPeakAbsolute;
    private long _speakerDiagnosticFrames;
    private long _speakerDiagnosticNonSilentFrames;
    private long _speakerDiagnosticLastArrivalTimestamp;
    private double _speakerDiagnosticMaximumArrivalGapMs;
    private int _speakerDiagnosticBurstIntervals;
    private int _speakerDiagnosticLateIntervals;
    private bool _speakerDiagnosticActive;
    private string _pendingUserTranscript = string.Empty;
    private int _mediaSummaryLogged;
    private int _speakerSessionDiagnosticCompleted;
    private int _speakerSessionDiagnosticFramesInSegment;
    private int _speakerSessionDiagnosticPart;
    private long _speakerSessionDiagnosticTotalFrames;
    private DateTimeOffset _speakerSessionDiagnosticStartedAt;
    private int _spokenHangupRequested;
    private int _started;
    private int _disposed;
    private Task? _stopTask;

    public WebView2VoiceDuplexAudioSession(
        SynchronizationContext uiContext,
        string userDataDirectory,
        Func<string, CancellationToken, Task<string>> negotiate,
        Action mediaConnected,
        Task realtimeReady,
        Task realtimeCompletion,
        Func<CancellationToken, Task> stopRealtime,
        Func<ValueTask> disposeRealtime,
        int conversationSpeakerGain,
        bool preserveAssistantAudioDiagnostics = false,
        Action<CodexVoiceConversationKind, string, bool>? transcriptChanged = null,
        Action<string>? activityChanged = null,
        Action<string>? log = null,
        string? diagnosticsDirectory = null)
    {
        _uiContext = uiContext ?? throw new ArgumentNullException(nameof(uiContext));
        _userDataDirectory = Path.GetFullPath(userDataDirectory);
        _negotiate = negotiate ?? throw new ArgumentNullException(nameof(negotiate));
        _mediaConnected = mediaConnected ?? throw new ArgumentNullException(nameof(mediaConnected));
        _realtimeReady = realtimeReady ?? throw new ArgumentNullException(nameof(realtimeReady));
        _realtimeCompletion = realtimeCompletion ?? throw new ArgumentNullException(nameof(realtimeCompletion));
        _stopRealtime = stopRealtime ?? throw new ArgumentNullException(nameof(stopRealtime));
        _disposeRealtime = disposeRealtime ?? throw new ArgumentNullException(nameof(disposeRealtime));
        if (conversationSpeakerGain is < VoicePePreferences.MinimumConversationSpeakerGain
            or > VoicePePreferences.MaximumConversationSpeakerGain)
        {
            throw new ArgumentOutOfRangeException(nameof(conversationSpeakerGain));
        }
        _conversationSpeakerGain = conversationSpeakerGain;
        _preserveAssistantAudioDiagnostics = preserveAssistantAudioDiagnostics;
        _diagnosticsDirectory = string.IsNullOrWhiteSpace(diagnosticsDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Joydex",
                "voice-diagnostics")
            : Path.GetFullPath(diagnosticsDirectory);
        _transcriptChanged = transcriptChanged;
        _activityChanged = activityChanged;
        _log = log;
    }

    public VoicePcmFormat MicrophoneInputFormat => MicrophoneFormat;

    public VoicePcmFormat SpeakerOutputFormat => SpeakerFormat;

    public Task Completion => _completion.Task;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The WebRTC audio session has already started.");
        }

        try
        {
            await InitializeBrowserAsync(cancellationToken).ConfigureAwait(false);
            await _browserReady.Task.WaitAsync(BrowserReadyTimeout, cancellationToken).ConfigureAwait(false);
            await PostMessageAsync(new { type = "start" }, cancellationToken).ConfigureAwait(false);
            var offer = await _offer.Task.WaitAsync(PeerConnectTimeout, cancellationToken).ConfigureAwait(false);
            var answer = await _negotiate(offer, cancellationToken).ConfigureAwait(false);
            await PostMessageAsync(new { type = "answer", sdp = answer }, cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(_peerConnected.Task, _dataChannelOpen.Task, _remoteTrackReady.Task)
                .WaitAsync(PeerConnectTimeout, cancellationToken)
                .ConfigureAwait(false);
            _mediaConnected();
            await _realtimeReady.WaitAsync(PeerConnectTimeout, cancellationToken).ConfigureAwait(false);
            _ = ObserveRealtimeCompletionAsync();
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
            _speakerOutput.Writer.TryComplete(exception);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask SendMicrophoneFrameAsync(
        VoicePcmFrame frame,
        CancellationToken cancellationToken = default)
    {
        frame.Validate(MicrophoneFormat);
        if (frame.Payload.Length != MicrophoneFrameBytes)
        {
            throw new ArgumentException(
                $"WebRTC microphone frames must be exactly {MicrophoneFrameBytes} bytes (20 ms).",
                nameof(frame));
        }

        var amplifiedPayload = ApplyMicrophoneGain(frame.Payload.Span, ConversationMicrophoneGain);
        var pcm = Convert.ToBase64String(amplifiedPayload);
        if (Interlocked.Increment(ref _microphoneFramesSent) == 1)
        {
            _log?.Invoke(
                $"Codex WebRTC microphone input received its first 16 kHz PCM frame; "
                + $"conversationGain={ConversationMicrophoneGain}x.");
        }

        await PostMessageAsync(
                new
                {
                    type = "microphone",
                    sampleRate = MicrophoneFormat.SampleRate,
                    sequence = frame.SequenceNumber,
                    pcm,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<VoiceSpeakerOutput> ReadSpeakerOutputAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var output in _speakerOutput.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return output;
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopGate)
        {
            return new ValueTask(_stopTask ??= StopCoreAsync(cancellationToken));
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await _stopRealtime(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _log?.Invoke($"Codex Realtime stop did not complete cleanly: {exception.Message}");
            }

            try
            {
                await PostMessageAsync(new { type = "stop" }, cancellationToken).ConfigureAwait(false);
                await _browserStopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _log?.Invoke($"WebRTC peer stop did not complete cleanly: {exception.Message}");
            }
        }
        finally
        {
            await CompleteSpeakerSessionDiagnosticCaptureAsync().ConfigureAwait(false);
            LogMediaSummary();
            _speakerOutput.Writer.TryComplete();
            _completion.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await RunOnUiAsync(
                    () =>
                    {
                        var webView = _webView;
                        var form = _form;
                        _webView = null;
                        _form = null;
                        if (webView?.CoreWebView2 is not null)
                        {
                            webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                        }

                        webView?.Dispose();
                        form?.Close();
                        form?.Dispose();
                        return Task.CompletedTask;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            await _disposeRealtime().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        GC.SuppressFinalize(this);
        if (failure is not null)
        {
            throw failure;
        }
    }

    private async Task InitializeBrowserAsync(CancellationToken cancellationToken)
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Voice");
        var index = Path.Combine(assets, "index.html");
        var worklet = Path.Combine(assets, "duplex-processor.js");
        if (!File.Exists(index) || !File.Exists(worklet))
        {
            throw new FileNotFoundException("Joydex Voice WebRTC assets are missing from the application directory.");
        }

        Directory.CreateDirectory(_userDataDirectory);
        await RunOnUiAsync(
                async () =>
                {
                    _form = new OffscreenVoiceForm();
                    _webView = new WebView2 { Dock = DockStyle.Fill, Visible = true };
                    _form.Controls.Add(_webView);
                    _form.Show();

                    var options = new CoreWebView2EnvironmentOptions(
                        additionalBrowserArguments: "--autoplay-policy=no-user-gesture-required");
                    var environment = await CoreWebView2Environment.CreateAsync(
                        browserExecutableFolder: null,
                        userDataFolder: _userDataDirectory,
                        options: options);
                    await _webView.EnsureCoreWebView2Async(environment);
                    _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    _webView.CoreWebView2.IsMuted = true;
                    _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "joydex.voice",
                        assets,
                        CoreWebView2HostResourceAccessKind.DenyCors);
                    _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                    _webView.CoreWebView2.Navigate("https://joydex.voice/index.html");
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using var document = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            switch (type)
            {
                case "ready":
                    _log?.Invoke("Joydex WebRTC browser is ready.");
                    _browserReady.TrySetResult(true);
                    break;

                case "offer":
                    if (root.TryGetProperty("sdp", out var sdpElement)
                        && sdpElement.GetString() is { Length: > 0 } sdp)
                    {
                        _offer.TrySetResult(sdp);
                    }
                    break;

                case "connected":
                    _log?.Invoke("Joydex WebRTC peer connection is connected.");
                    _peerConnected.TrySetResult(true);
                    break;

                case "data-channel-open":
                    _log?.Invoke("Joydex WebRTC Realtime data channel is open.");
                    _dataChannelOpen.TrySetResult(true);
                    break;

                case "remote-track":
                    _log?.Invoke("Joydex WebRTC remote audio track and capture worklet are ready.");
                    _remoteTrackReady.TrySetResult(true);
                    break;

                case "remote-audio-playing":
                    _log?.Invoke("Joydex WebRTC remote audio element entered playback.");
                    break;

                case "stopped":
                    _browserStopped.TrySetResult(true);
                    break;

                case "output-started":
                    _log?.Invoke("Codex Realtime reported assistant audio playback started.");
                    _activityChanged?.Invoke("Assistant is speaking");
                    BeginSpeakerDiagnosticCapture();
                    break;

                case "output-stopped":
                    _log?.Invoke("Codex Realtime reported assistant audio playback stopped.");
                    break;

                case "output-cleared":
                    _log?.Invoke("Codex Realtime reported assistant audio playback cleared.");
                    break;

                case "realtime-event":
                    LogRealtimeEvent(root);
                    break;

                case "user-transcript-added":
                    if (root.TryGetProperty("transcript", out var addedTranscriptElement)
                        && addedTranscriptElement.GetString() is { Length: > 0 } addedTranscript)
                    {
                        _pendingUserTranscript = MergeUserTranscript(_pendingUserTranscript, addedTranscript);
                        _transcriptChanged?.Invoke(CodexVoiceConversationKind.User, addedTranscript, false);
                    }
                    break;

                case "assistant-transcript-added":
                    if (root.TryGetProperty("transcript", out var assistantTranscriptElement)
                        && assistantTranscriptElement.GetString() is { Length: > 0 } assistantTranscript)
                    {
                        _transcriptChanged?.Invoke(
                            CodexVoiceConversationKind.Assistant,
                            assistantTranscript,
                            false);
                    }
                    break;

                case "user-turn-done":
                    var completedTranscript = root.TryGetProperty("transcript", out var transcriptElement)
                        ? transcriptElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(completedTranscript))
                    {
                        completedTranscript = _pendingUserTranscript;
                    }
                    _pendingUserTranscript = string.Empty;
                    _transcriptChanged?.Invoke(
                        CodexVoiceConversationKind.User,
                        completedTranscript,
                        true);
                    var isSpokenHangup = IsSpokenHangupCommand(completedTranscript);
                    _log?.Invoke(
                        $"Joydex received a completed user transcript for local controls; "
                        + $"length={completedTranscript.Length}; spokenHangup={isSpokenHangup}.");
                    if (isSpokenHangup && Interlocked.Exchange(ref _spokenHangupRequested, 1) == 0)
                    {
                        _log?.Invoke("Joydex recognized a spoken hangup command; ending the room Voice Session.");
                        _ = StopFromSpokenHangupAsync();
                    }
                    break;

                case "assistant-turn-done":
                    var completedAssistantTranscript = root.TryGetProperty(
                            "transcript",
                            out var assistantDoneElement)
                        ? assistantDoneElement.GetString() ?? string.Empty
                        : string.Empty;
                    _transcriptChanged?.Invoke(
                        CodexVoiceConversationKind.Assistant,
                        completedAssistantTranscript,
                        true);
                    break;

                case "rtc-stats":
                    LogRtcStats(root);
                    break;

                case "rtc-stats-error":
                    var statsError = root.TryGetProperty("message", out var statsMessage)
                        ? statsMessage.GetString()
                        : "unknown error";
                    _log?.Invoke($"Joydex WebRTC RTP diagnostics failed: {statsError}");
                    break;

                case "data-channel-closed":
                    if (!_dataChannelOpen.Task.IsCompletedSuccessfully)
                    {
                        Fail(new InvalidOperationException("The Realtime data channel closed before media became ready."));
                    }
                    else
                    {
                        _ = ObserveDataChannelCloseAsync();
                    }
                    break;

                case "speaker":
                    ReceiveSpeakerFrame(root);
                    break;

                case "playback-ended":
                    LogSpeakerBoundary("ended");
                    CompleteSpeakerDiagnosticCapture("ended");
                    EnqueueSpeakerOutput(VoiceSpeakerOutput.Ended);
                    break;

                case "playback-cleared":
                    LogSpeakerBoundary("cleared");
                    CompleteSpeakerDiagnosticCapture("cleared");
                    EnqueueSpeakerOutput(VoiceSpeakerOutput.Cleared);
                    break;

                case "error":
                    var message = root.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : "Unknown WebRTC media error.";
                    Fail(new InvalidOperationException(message));
                    break;
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void ReceiveSpeakerFrame(JsonElement message)
    {
        if (!message.TryGetProperty("pcm", out var pcmElement)
            || pcmElement.GetString() is not { Length: > 0 } pcm)
        {
            return;
        }

        var (decodedPayload, payload) = PrepareSpeakerPcm(pcm, _conversationSpeakerGain);

        var sequence = message.TryGetProperty("sequence", out var sequenceElement)
                       && sequenceElement.TryGetInt64(out var suppliedSequence)
            ? suppliedSequence
            : Interlocked.Increment(ref _speakerSequence) - 1;
        var peak = PeakAbsolutePcm16(payload);
        UpdateMaximum(ref _speakerPeakAbsolute, peak);
        var count = Interlocked.Increment(ref _speakerFramesReceived);
        if (peak > 0)
        {
            var nonSilentCount = Interlocked.Increment(ref _speakerNonSilentFramesReceived);
            if (nonSilentCount == 1)
            {
                _log?.Invoke(
                    $"Joydex decoded its first non-silent Codex speaker frame; peak={peak}; "
                    + $"conversationGain={_conversationSpeakerGain}x.");
            }
        }
        else if (count == 1)
        {
            _log?.Invoke("Joydex decoded its first Codex speaker frame; the frame was silent.");
        }

        RecordSpeakerSessionDiagnosticFrame(decodedPayload);
        RecordSpeakerDiagnosticFrame(payload, peak);
        EnqueueSpeakerOutput(VoiceSpeakerOutput.Audio(new VoicePcmFrame(sequence, payload)));
    }

    internal static Channel<VoiceSpeakerOutput> CreateSpeakerOutputChannel() =>
        Channel.CreateBounded<VoiceSpeakerOutput>(
            new BoundedChannelOptions(MaximumBufferedSpeakerOutputs)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

    private void EnqueueSpeakerOutput(VoiceSpeakerOutput output)
    {
        if (_speakerOutput.Writer.TryWrite(output))
        {
            return;
        }

        Fail(
            new InvalidOperationException(
                $"Codex speaker input exceeded the bounded {MaximumBufferedSpeakerOutputs}-item host queue."));
    }

    private void BeginSpeakerDiagnosticCapture()
    {
        _speakerDiagnosticPcm.SetLength(0);
        _speakerDiagnosticFrames = 0;
        _speakerDiagnosticNonSilentFrames = 0;
        _speakerDiagnosticLastArrivalTimestamp = 0;
        _speakerDiagnosticMaximumArrivalGapMs = 0;
        _speakerDiagnosticBurstIntervals = 0;
        _speakerDiagnosticLateIntervals = 0;
        _speakerDiagnosticActive = true;
    }

    private void RecordSpeakerSessionDiagnosticFrame(byte[] payload)
    {
        if (!_preserveAssistantAudioDiagnostics)
        {
            return;
        }

        lock (_speakerSessionDiagnosticGate)
        {
            if (_speakerSessionDiagnosticCompleted != 0)
            {
                return;
            }
            if (_speakerSessionDiagnosticStartedAt == default)
            {
                _speakerSessionDiagnosticStartedAt = DateTimeOffset.Now;
            }
            if (_speakerSessionDiagnosticFramesInSegment >= SpeakerSessionDiagnosticSegmentFrames)
            {
                QueueSpeakerSessionDiagnosticSegmentLocked();
            }

            _speakerSessionDiagnosticPcm.Write(payload);
            _speakerSessionDiagnosticFramesInSegment++;
            _speakerSessionDiagnosticTotalFrames++;
        }
    }

    private void RecordSpeakerDiagnosticFrame(byte[] payload, int peak)
    {
        if (!_speakerDiagnosticActive)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_speakerDiagnosticLastArrivalTimestamp != 0)
        {
            var gapMs = Stopwatch.GetElapsedTime(_speakerDiagnosticLastArrivalTimestamp, now).TotalMilliseconds;
            _speakerDiagnosticMaximumArrivalGapMs = Math.Max(_speakerDiagnosticMaximumArrivalGapMs, gapMs);
            if (gapMs < 5)
            {
                _speakerDiagnosticBurstIntervals++;
            }
            if (gapMs > 35)
            {
                _speakerDiagnosticLateIntervals++;
            }
        }
        _speakerDiagnosticLastArrivalTimestamp = now;
        _speakerDiagnosticFrames++;
        if (peak > 0)
        {
            _speakerDiagnosticNonSilentFrames++;
        }
        if (_preserveAssistantAudioDiagnostics)
        {
            _speakerDiagnosticPcm.Write(payload);
        }
    }

    private void CompleteSpeakerDiagnosticCapture(string boundary)
    {
        if (!_speakerDiagnosticActive)
        {
            return;
        }
        _speakerDiagnosticActive = false;

        var pcm = _preserveAssistantAudioDiagnostics ? _speakerDiagnosticPcm.ToArray() : [];
        var capturedAt = DateTimeOffset.Now;
        var directory = _diagnosticsDirectory;
        var path = Path.Combine(directory, $"speaker-{capturedAt:yyyyMMdd-HHmmss-fff}.wav");
        var frames = _speakerDiagnosticFrames;
        var nonSilentFrames = _speakerDiagnosticNonSilentFrames;
        var maximumGapMs = _speakerDiagnosticMaximumArrivalGapMs;
        var burstIntervals = _speakerDiagnosticBurstIntervals;
        var lateIntervals = _speakerDiagnosticLateIntervals;
        _log?.Invoke(
            $"Joydex assistant audio diagnostic summary; boundary={boundary}; frames={frames}; "
            + $"nonSilentFrames={nonSilentFrames}; maxArrivalGapMs={maximumGapMs:F1}; "
            + $"burstIntervals={burstIntervals}; lateIntervals={lateIntervals}; "
            + $"wavPreserved={_preserveAssistantAudioDiagnostics}.");

        if (!_preserveAssistantAudioDiagnostics)
        {
            return;
        }

        _ = Task.Run(
            () =>
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(path, CreatePcm16MonoWave(pcm, SpeakerFormat.SampleRate));
                }
                catch (Exception exception)
                {
                    _log?.Invoke($"Could not write Joydex assistant PCM diagnostic: {exception.Message}");
                }
            });
    }

    private async Task CompleteSpeakerSessionDiagnosticCaptureAsync()
    {
        if (!_preserveAssistantAudioDiagnostics)
        {
            return;
        }

        Task[] writes;
        long frames;
        int parts;
        lock (_speakerSessionDiagnosticGate)
        {
            if (_speakerSessionDiagnosticCompleted != 0)
            {
                return;
            }
            _speakerSessionDiagnosticCompleted = 1;
            QueueSpeakerSessionDiagnosticSegmentLocked();
            writes = [.. _speakerSessionDiagnosticWrites];
            frames = _speakerSessionDiagnosticTotalFrames;
            parts = _speakerSessionDiagnosticPart;
        }

        if (writes.Length == 0)
        {
            _log?.Invoke("Joydex continuous assistant audio diagnostic contained no decoded PCM frames.");
            return;
        }

        await Task.WhenAll(writes).ConfigureAwait(false);
        _log?.Invoke(
            $"Joydex preserved continuous raw assistant audio diagnostic; frames={frames}; parts={parts}.");
    }

    private void QueueSpeakerSessionDiagnosticSegmentLocked()
    {
        if (_speakerSessionDiagnosticFramesInSegment == 0)
        {
            return;
        }

        var pcm = _speakerSessionDiagnosticPcm.ToArray();
        _speakerSessionDiagnosticPcm.SetLength(0);
        _speakerSessionDiagnosticFramesInSegment = 0;
        var part = ++_speakerSessionDiagnosticPart;
        var capturedAt = _speakerSessionDiagnosticStartedAt;
        var directory = _diagnosticsDirectory;
        var path = Path.Combine(
            directory,
            $"speaker-session-raw-{capturedAt:yyyyMMdd-HHmmss-fff}-part{part:D3}.wav");
        _speakerSessionDiagnosticWrites.Add(
            Task.Run(
                () =>
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        File.WriteAllBytes(path, CreatePcm16MonoWave(pcm, SpeakerFormat.SampleRate));
                        _log?.Invoke(
                            $"Joydex preserved continuous raw assistant audio segment; "
                            + $"frames={pcm.Length / SpeakerFrameBytes}; path={path}.");
                    }
                    catch (Exception exception)
                    {
                        _log?.Invoke(
                            $"Could not write Joydex continuous raw assistant PCM diagnostic: {exception.Message}");
                    }
                }));
    }

    internal static byte[] CreatePcm16MonoWave(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        if (pcm.Length % sizeof(short) != 0)
        {
            throw new ArgumentException("PCM16 diagnostics must contain complete samples.", nameof(pcm));
        }
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        using var wave = new MemoryStream(44 + pcm.Length);
        using (var writer = new BinaryWriter(wave, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(checked(36 + pcm.Length));
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(checked(sampleRate * sizeof(short)));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }
        return wave.ToArray();
    }

    private void LogRealtimeEvent(JsonElement message)
    {
        var eventType = message.TryGetProperty("eventType", out var typeElement)
            ? typeElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(eventType)
            || eventType.Length > 120
            || eventType.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            eventType = "invalid";
        }
        var count = message.TryGetProperty("eventCount", out var countElement)
                    && countElement.TryGetInt32(out var suppliedCount)
            ? suppliedCount
            : 0;
        var eventRole = message.TryGetProperty("eventRole", out var roleElement)
            ? roleElement.GetString()
            : null;
        if (eventRole is not ("user" or "assistant"))
        {
            eventRole = "none";
        }
        _log?.Invoke($"Codex Realtime data-channel event; type={eventType}; role={eventRole}; count={count}.");
    }

    private void LogRtcStats(JsonElement message)
    {
        static long ReadInt64(JsonElement root, string name) =>
            root.TryGetProperty(name, out var element) && element.TryGetInt64(out var value) ? value : 0;
        static double ReadDouble(JsonElement root, string name) =>
            root.TryGetProperty(name, out var element) && element.TryGetDouble(out var value) ? value : 0;

        _log?.Invoke(
            $"Joydex WebRTC outbound audio stats; packets={ReadInt64(message, "packetsSent")}; "
            + $"bytes={ReadInt64(message, "bytesSent")}; audioLevel={ReadDouble(message, "audioLevel"):F4}; "
            + $"energy={ReadDouble(message, "totalAudioEnergy"):F4}; "
            + $"sampleSeconds={ReadDouble(message, "totalSamplesDuration"):F2}; "
            + $"inboundPackets={ReadInt64(message, "packetsReceived")}; "
            + $"inboundBytes={ReadInt64(message, "bytesReceived")}; "
            + $"inboundEnergy={ReadDouble(message, "inboundAudioEnergy"):F4}; "
            + $"inboundSampleSeconds={ReadDouble(message, "inboundSamplesDuration"):F2}.");
    }

    private async Task StopFromSpokenHangupAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"Spoken hangup did not stop the room Voice Session cleanly: {exception.Message}");
        }
    }

    internal static string MergeUserTranscript(string existing, string candidate)
    {
        existing = existing.Trim();
        candidate = candidate.Trim();
        if (candidate.Length == 0)
        {
            return existing;
        }
        if (existing.Length == 0)
        {
            return candidate[..Math.Min(candidate.Length, MaximumControlTranscriptLength)];
        }
        if (candidate.StartsWith(existing, StringComparison.OrdinalIgnoreCase))
        {
            return candidate[..Math.Min(candidate.Length, MaximumControlTranscriptLength)];
        }
        if (existing.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        var merged = $"{existing} {candidate}";
        return merged[..Math.Min(merged.Length, MaximumControlTranscriptLength)];
    }

    internal static bool IsSpokenHangupCommand(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        var normalized = new string(
                transcript
                    .Trim()
                    .ToLowerInvariant()
                    .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
                    .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>(normalized);
        RemovePrefix(words, ["hey", "computer"]);
        RemovePrefix(words, ["computer"]);
        RemovePrefix(words, ["can", "you"]);
        RemovePrefix(words, ["could", "you"]);
        RemovePrefix(words, ["would", "you"]);
        RemovePrefix(words, ["will", "you"]);
        RemovePrefix(words, ["please"]);
        RemovePrefix(words, ["okay"]);
        RemovePrefix(words, ["alright"]);
        RemovePrefix(words, ["well"]);
        RemoveSpokenHangupFillers(words);

        var command = string.Join(' ', words);
        if (command is
            "hang up" or
            "hangup" or
            "hang up the call" or
            "hang up this call" or
            "hang up the voice chat" or
            "hang up this voice chat" or
            "cancel" or
            "cancel the call" or
            "cancel this call" or
            "cancel the voice chat" or
            "cancel this voice chat" or
            "end call" or
            "end the call" or
            "end this call" or
            "end conversation" or
            "end the conversation" or
            "end this conversation" or
            "end voice chat" or
            "end the voice chat" or
            "end this voice chat" or
            "stop the voice chat" or
            "stop this voice chat" or
            "goodbye" or
            "good bye" or
            "bye" or
            "bye bye" or
            "shut up" or
            "end" or
            "die")
        {
            return true;
        }

        string[][] terminalCommands =
        [
            ["hang", "up"],
            ["hangup"],
            ["hang", "up", "the", "call"],
            ["hang", "up", "this", "call"],
            ["hang", "up", "the", "voice", "chat"],
            ["hang", "up", "this", "voice", "chat"],
            ["cancel"],
            ["cancel", "the", "call"],
            ["cancel", "this", "call"],
            ["cancel", "the", "voice", "chat"],
            ["cancel", "this", "voice", "chat"],
            ["end", "call"],
            ["end", "the", "call"],
            ["end", "this", "call"],
            ["end", "conversation"],
            ["end", "the", "conversation"],
            ["end", "this", "conversation"],
            ["end", "voice", "chat"],
            ["end", "the", "voice", "chat"],
            ["end", "this", "voice", "chat"],
            ["stop", "the", "voice", "chat"],
            ["stop", "this", "voice", "chat"],
        ];
        foreach (var terminalCommand in terminalCommands)
        {
            if (!words.TakeLast(terminalCommand.Length).SequenceEqual(terminalCommand))
            {
                continue;
            }

            var commandStart = words.Count - terminalCommand.Length;
            var preceding = words.Take(commandStart).ToArray();
            var negated = preceding.Contains("not")
                          || preceding.Contains("never")
                          || preceding.Zip(preceding.Skip(1)).Any(pair =>
                              pair.First == "don" && pair.Second == "t");
            return !negated;
        }

        return false;
    }

    private static void RemoveSpokenHangupFillers(List<string> words)
    {
        string[][] suffixes =
        [
            ["please"],
            ["now"],
            ["thanks"],
            ["thank", "you"],
            ["okay"],
            ["alright"],
            ["for", "me"],
        ];
        var removed = true;
        while (removed)
        {
            removed = false;
            foreach (var suffix in suffixes)
            {
                if (words.Count < suffix.Length || !words.TakeLast(suffix.Length).SequenceEqual(suffix))
                {
                    continue;
                }

                words.RemoveRange(words.Count - suffix.Length, suffix.Length);
                removed = true;
                break;
            }
        }
    }

    private static void RemovePrefix(List<string> words, IReadOnlyList<string> prefix)
    {
        if (words.Count >= prefix.Count && words.Take(prefix.Count).SequenceEqual(prefix))
        {
            words.RemoveRange(0, prefix.Count);
        }
    }

    private static void RemoveSuffix(List<string> words, IReadOnlyList<string> suffix)
    {
        if (words.Count >= suffix.Count && words.TakeLast(suffix.Count).SequenceEqual(suffix))
        {
            words.RemoveRange(words.Count - suffix.Count, suffix.Count);
        }
    }

    private void LogSpeakerBoundary(string boundary)
    {
        _log?.Invoke(
            $"Codex speaker playback {boundary}; frames={Interlocked.Read(ref _speakerFramesReceived)}, "
            + $"nonSilentFrames={Interlocked.Read(ref _speakerNonSilentFramesReceived)}, "
            + $"peak={Volatile.Read(ref _speakerPeakAbsolute)}.");
    }

    private void LogMediaSummary()
    {
        if (Interlocked.Exchange(ref _mediaSummaryLogged, 1) != 0)
        {
            return;
        }

        _log?.Invoke(
            $"Joydex WebRTC media summary: microphoneFrames={Interlocked.Read(ref _microphoneFramesSent)}, "
            + $"speakerFrames={Interlocked.Read(ref _speakerFramesReceived)}, "
            + $"nonSilentSpeakerFrames={Interlocked.Read(ref _speakerNonSilentFramesReceived)}, "
            + $"speakerPeak={Volatile.Read(ref _speakerPeakAbsolute)}.");
    }

    private static int PeakAbsolutePcm16(byte[] payload)
    {
        var peak = 0;
        for (var offset = 0; offset < payload.Length; offset += sizeof(short))
        {
            var sample = (short)(payload[offset] | (payload[offset + 1] << 8));
            var absolute = sample == short.MinValue ? 32_768 : Math.Abs(sample);
            peak = Math.Max(peak, absolute);
        }

        return peak;
    }

    internal static byte[] ApplyMicrophoneGain(ReadOnlySpan<byte> payload, int gain)
        => ApplyPcm16Gain(payload, gain);

    internal static (byte[] Decoded, byte[] Playout) PrepareSpeakerPcm(string pcm, int gain)
    {
        var decoded = Convert.FromBase64String(pcm);
        if (decoded.Length != SpeakerFrameBytes)
        {
            throw new InvalidDataException(
                $"WebRTC speaker frame contained {decoded.Length} bytes; expected {SpeakerFrameBytes}.");
        }

        return (decoded, ApplyPcm16Gain(decoded, gain));
    }

    internal static byte[] ApplyPcm16Gain(ReadOnlySpan<byte> payload, int gain)
    {
        if (payload.Length % sizeof(short) != 0)
        {
            throw new ArgumentException("PCM16 payloads must contain complete samples.", nameof(payload));
        }
        if (gain < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gain), "PCM gain must be at least one.");
        }

        var amplified = new byte[payload.Length];
        for (var offset = 0; offset < payload.Length; offset += sizeof(short))
        {
            var sample = (short)(payload[offset] | (payload[offset + 1] << 8));
            var adjusted = Math.Clamp(sample * gain, short.MinValue, short.MaxValue);
            amplified[offset] = (byte)adjusted;
            amplified[offset + 1] = (byte)(adjusted >> 8);
        }
        return amplified;
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private async Task ObserveRealtimeCompletionAsync()
    {
        try
        {
            await _realtimeCompletion.ConfigureAwait(false);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void Fail(Exception exception)
    {
        _browserReady.TrySetException(exception);
        _offer.TrySetException(exception);
        _peerConnected.TrySetException(exception);
        _dataChannelOpen.TrySetException(exception);
        _remoteTrackReady.TrySetException(exception);
        _speakerOutput.Writer.TryComplete(exception);
        _completion.TrySetException(exception);
    }

    private async Task ObserveDataChannelCloseAsync()
    {
        try
        {
            await _realtimeCompletion.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Fail(new InvalidOperationException("The Realtime data channel closed while the Voice Session was active."));
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private Task PostMessageAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        return RunOnUiAsync(
            () =>
            {
                var core = _webView?.CoreWebView2
                    ?? throw new InvalidOperationException("The Joydex WebRTC browser is not initialized.");
                core.PostWebMessageAsJson(json);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private Task RunOnUiAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(
            async _ =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            null);
        return completion.Task;
    }

    private sealed class OffscreenVoiceForm : Form
    {
        public OffscreenVoiceForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            Location = new Point(-30_000, -30_000);
            Opacity = 0.01;
            ShowInTaskbar = false;
            Size = new Size(2, 2);
            StartPosition = FormStartPosition.Manual;
            Text = "Joydex Voice Media";
        }

        protected override bool ShowWithoutActivation => true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
