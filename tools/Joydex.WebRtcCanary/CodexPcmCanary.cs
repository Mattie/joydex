using System.Diagnostics;
using System.Text.Json;

namespace Joydex.WebRtcCanary;

internal sealed class CodexPcmCanary : IAsyncDisposable
{
    private const int RequiredSampleRate = 24_000;
    private const short RequiredChannels = 1;
    private const int ChunkMilliseconds = 20;
    private const int SamplesPerChunk = RequiredSampleRate * ChunkMilliseconds / 1000;
    private const int BytesPerChunk = SamplesPerChunk * RequiredChannels * 2;

    private readonly CodexAppServerClient _client;
    private readonly string? _configuredThreadId;
    private readonly string _configuredThreadTitle;
    private readonly TaskCompletionSource<bool> _started = NewCompletion<bool>();
    private readonly TaskCompletionSource<string> _userTranscript = NewCompletion<string>();
    private readonly TaskCompletionSource<string> _assistantTranscript = NewCompletion<string>();
    private readonly TaskCompletionSource<bool> _firstOutputAudio = NewCompletion<bool>();
    private readonly TaskCompletionSource<string?> _closed = NewCompletion<string?>();
    private readonly MemoryStream _outputPcm = new();
    private readonly object _outputGate = new();
    private string? _activeThreadId;
    private int? _outputSampleRate;
    private short? _outputChannels;
    private int _outputChunks;

    public CodexPcmCanary(
        CodexAppServerClient client,
        string? configuredThreadId,
        string configuredThreadTitle)
    {
        _client = client;
        _configuredThreadId = configuredThreadId;
        _configuredThreadTitle = configuredThreadTitle;
        _client.NotificationReceived += OnNotification;
    }

    public async Task RunAsync(string inputWavPath, string outputWavPath, CancellationToken cancellationToken)
    {
        var input = PcmWaveFile.Read16BitPcm(inputWavPath);
        if (input.SampleRate != RequiredSampleRate || input.Channels != RequiredChannels)
        {
            throw new InvalidDataException(
                $"PCM canary input must be {RequiredSampleRate} Hz mono; got {input.SampleRate} Hz, {input.Channels} channel(s).");
        }

        Report("input-ready", new
        {
            path = Path.GetFullPath(inputWavPath),
            input.SampleRate,
            input.Channels,
            durationSeconds = input.Data.Length / (double)(input.SampleRate * input.BytesPerSampleFrame),
        });

        var threadId = await ResolveThreadIdAsync(cancellationToken).ConfigureAwait(false);
        _activeThreadId = threadId;

        Report("resuming-thread", new { threadId, title = _configuredThreadTitle });
        await _client.RequestAsync(
            "thread/resume",
            new { threadId, excludeTurns = true },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        Report("starting-realtime", new { threadId, version = "v3", transport = "websocket" });
        await _client.RequestAsync(
            "thread/realtime/start",
            new
            {
                threadId,
                realtimeSessionId = $"joydex-pcm-canary-{Guid.NewGuid():N}",
                version = "v3",
                outputModality = "audio",
                includeStartupContext = false,
                prompt = "Follow the user's spoken request literally. Keep the spoken response brief.",
                transport = new { type = "websocket" },
            },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        await _started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        Report("realtime-started", new { threadId });

        await SendSilenceAsync(threadId, TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        await SendPcmAsync(threadId, input.Data, cancellationToken).ConfigureAwait(false);
        await SendSilenceAsync(threadId, TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
        Report("input-complete", new { bytes = input.Data.Length });

        var userTranscript = await _userTranscript.Task
            .WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        Report("user-transcript", new { text = userTranscript });

        await _firstOutputAudio.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        var assistantTranscript = await _assistantTranscript.Task
            .WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        Report("assistant-transcript", new { text = assistantTranscript });

        await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
        byte[] outputBytes;
        int sampleRate;
        short channels;
        int outputChunks;
        lock (_outputGate)
        {
            outputBytes = _outputPcm.ToArray();
            sampleRate = _outputSampleRate
                ?? throw new InvalidOperationException("Realtime returned no output sample rate.");
            channels = _outputChannels
                ?? throw new InvalidOperationException("Realtime returned no output channel count.");
            outputChunks = _outputChunks;
        }

        PcmWaveFile.Write16BitPcm(outputWavPath, sampleRate, channels, outputBytes);
        Report("output-written", new
        {
            path = Path.GetFullPath(outputWavPath),
            bytes = outputBytes.Length,
            chunks = outputChunks,
            sampleRate,
            channels,
            durationSeconds = outputBytes.Length / (double)(sampleRate * channels * 2),
        });

        await _client.RequestAsync(
            "thread/realtime/stop",
            new { threadId },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        var closeReason = await _closed.Task
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        var expectedPhrasePresent = assistantTranscript.Contains(
            "Joydex PCM round trip connected",
            StringComparison.OrdinalIgnoreCase);
        var result = new
        {
            passed = outputBytes.Length > 0 && expectedPhrasePresent,
            threadId,
            userTranscript,
            assistantTranscript,
            expectedPhrasePresent,
            outputWav = Path.GetFullPath(outputWavPath),
            outputBytes = outputBytes.Length,
            outputChunks,
            outputSampleRate = sampleRate,
            outputChannels = channels,
            closeReason,
        };
        Console.WriteLine($"PCM_CANARY_RESULT {JsonSerializer.Serialize(result, JsonOptions)}");

        if (!result.passed)
        {
            throw new InvalidOperationException("PCM transport completed, but the expected spoken response was not confirmed.");
        }
    }

    private async Task SendPcmAsync(string threadId, byte[] pcm, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var chunkNumber = 0;
        for (var offset = 0; offset < pcm.Length; offset += BytesPerChunk)
        {
            var count = Math.Min(BytesPerChunk, pcm.Length - offset);
            if ((count & 1) != 0)
            {
                throw new InvalidDataException("PCM input ended with an incomplete 16-bit sample.");
            }

            await AppendAudioAsync(
                threadId,
                Convert.ToBase64String(pcm, offset, count),
                count / 2,
                cancellationToken).ConfigureAwait(false);
            chunkNumber++;

            var target = TimeSpan.FromMilliseconds(chunkNumber * ChunkMilliseconds);
            var delay = target - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task SendSilenceAsync(string threadId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var completeChunks = (int)Math.Ceiling(duration.TotalMilliseconds / ChunkMilliseconds);
        return SendPcmAsync(threadId, new byte[completeChunks * BytesPerChunk], cancellationToken);
    }

    private async Task AppendAudioAsync(
        string threadId,
        string base64Pcm,
        int samplesPerChannel,
        CancellationToken cancellationToken)
    {
        await _client.RequestAsync(
            "thread/realtime/appendAudio",
            new
            {
                threadId,
                audio = new
                {
                    data = base64Pcm,
                    sampleRate = RequiredSampleRate,
                    numChannels = RequiredChannels,
                    samplesPerChannel,
                    itemId = (string?)null,
                },
            },
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveThreadIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_configuredThreadId))
        {
            Report("thread-resolved", new { threadId = _configuredThreadId, title = _configuredThreadTitle });
            return _configuredThreadId;
        }

        Report("resolving-thread", new { title = _configuredThreadTitle });
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

        var selected = data.EnumerateArray()
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
            .FirstOrDefault();

        if (selected is null || string.IsNullOrWhiteSpace(selected.Id))
        {
            throw new InvalidOperationException(
                $"Could not find an active task named '{_configuredThreadTitle}'. Pass --thread-id to target it explicitly.");
        }

        Report("thread-resolved", new { threadId = selected.Id, title = selected.Name ?? _configuredThreadTitle });
        return selected.Id;
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        var notificationThreadId = parameters.ValueKind == JsonValueKind.Object &&
                                   parameters.TryGetProperty("threadId", out var threadIdElement)
            ? threadIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(_activeThreadId) ||
            !string.Equals(_activeThreadId, notificationThreadId, StringComparison.Ordinal))
        {
            return;
        }

        switch (method)
        {
            case "thread/realtime/started":
                _started.TrySetResult(true);
                break;

            case "thread/realtime/transcript/done":
                var role = parameters.GetProperty("role").GetString();
                var text = parameters.GetProperty("text").GetString() ?? string.Empty;
                if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    _userTranscript.TrySetResult(text);
                }
                else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    _assistantTranscript.TrySetResult(text);
                }

                break;

            case "thread/realtime/outputAudio/delta":
                CaptureOutputAudio(parameters);
                break;

            case "thread/realtime/error":
                var message = parameters.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString() ?? "Unknown realtime error."
                    : "Unknown realtime error.";
                FailPending(new InvalidOperationException(message));
                break;

            case "thread/realtime/closed":
                var reason = parameters.TryGetProperty("reason", out var reasonElement) &&
                             reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;
                _closed.TrySetResult(reason);
                break;
        }
    }

    private void CaptureOutputAudio(JsonElement parameters)
    {
        var audio = parameters.GetProperty("audio");
        var sampleRate = audio.GetProperty("sampleRate").GetInt32();
        var channelsValue = audio.GetProperty("numChannels").GetInt32();
        if (channelsValue is <= 0 or > short.MaxValue)
        {
            FailPending(new InvalidDataException($"Realtime returned invalid output channel count {channelsValue}."));
            return;
        }

        var channels = (short)channelsValue;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(audio.GetProperty("data").GetString() ?? string.Empty);
        }
        catch (FormatException exception)
        {
            FailPending(new InvalidDataException("Realtime returned invalid base64 output audio.", exception));
            return;
        }

        if (bytes.Length == 0 || bytes.Length % (channels * 2) != 0)
        {
            FailPending(new InvalidDataException("Realtime returned an incomplete signed 16-bit output sample frame."));
            return;
        }

        lock (_outputGate)
        {
            if ((_outputSampleRate is not null && _outputSampleRate != sampleRate) ||
                (_outputChannels is not null && _outputChannels != channels))
            {
                FailPending(new InvalidDataException("Realtime changed output audio format mid-session."));
                return;
            }

            _outputSampleRate = sampleRate;
            _outputChannels = channels;
            _outputPcm.Write(bytes);
            _outputChunks++;
        }

        _firstOutputAudio.TrySetResult(true);
    }

    private void FailPending(Exception exception)
    {
        _started.TrySetException(exception);
        _userTranscript.TrySetException(exception);
        _assistantTranscript.TrySetException(exception);
        _firstOutputAudio.TrySetException(exception);
        _closed.TrySetException(exception);
    }

    private static void Report(string phase, object detail) =>
        Console.WriteLine($"PCM_CANARY_STATE {JsonSerializer.Serialize(new { phase, detail }, JsonOptions)}");

    public ValueTask DisposeAsync()
    {
        _client.NotificationReceived -= OnNotification;
        _outputPcm.Dispose();
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
