using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>
/// Reads and writes persisted controls exposed by Joydex Voice PE firmware.
/// </summary>
public sealed class EspHomeVoicePeTuningClient : IDisposable
{
    private const string MicrophoneGainEntity = "joydex_wake_microphone_gain";
    private const string WakeProbabilityEntity = "joydex_wake_probability_cutoff";
    private const string SlidingWindowEntity = "joydex_wake_sliding_window";
    private const string VadProbabilityEntity = "joydex_vad_probability_cutoff";
    private const string BargeInEntity = "joydex_audio_barge_in";

    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan ApplyPollInterval = TimeSpan.FromMilliseconds(150);

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public EspHomeVoicePeTuningClient(Uri baseUri)
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, baseUri, ownsHttpClient: true)
    {
    }

    internal EspHomeVoicePeTuningClient(HttpClient httpClient, Uri baseUri)
        : this(httpClient, baseUri, ownsHttpClient: false)
    {
    }

    private EspHomeVoicePeTuningClient(HttpClient httpClient, Uri baseUri, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<VoicePeWakeTuning> GetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var gainTask = ReadNumberAsync(MicrophoneGainEntity, cancellationToken);
        var wakeProbabilityTask = ReadNumberAsync(WakeProbabilityEntity, cancellationToken);
        var slidingWindowTask = ReadNumberAsync(SlidingWindowEntity, cancellationToken);
        var vadProbabilityTask = ReadNumberAsync(VadProbabilityEntity, cancellationToken);
        await Task.WhenAll(gainTask, wakeProbabilityTask, slidingWindowTask, vadProbabilityTask)
            .ConfigureAwait(false);

        var tuning = new VoicePeWakeTuning(
            MicrophoneGain: ToInteger(await gainTask.ConfigureAwait(false), MicrophoneGainEntity),
            WakeProbabilityCutoff: await wakeProbabilityTask.ConfigureAwait(false),
            SlidingWindow: ToInteger(await slidingWindowTask.ConfigureAwait(false), SlidingWindowEntity),
            VadProbabilityCutoff: await vadProbabilityTask.ConfigureAwait(false));
        var errors = tuning.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "Voice PE returned wake tuning outside the supported Joydex range: "
                + string.Join(" ", errors));
        }

        return tuning;
    }

    public async Task<VoicePeWakeTuning> SetAsync(
        VoicePeWakeTuning tuning,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(tuning);
        var errors = tuning.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tuning), string.Join(" ", errors));
        }

        await WriteNumberAsync(MicrophoneGainEntity, tuning.MicrophoneGain, cancellationToken)
            .ConfigureAwait(false);
        await WriteNumberAsync(WakeProbabilityEntity, tuning.WakeProbabilityCutoff, cancellationToken)
            .ConfigureAwait(false);
        await WriteNumberAsync(VadProbabilityEntity, tuning.VadProbabilityCutoff, cancellationToken)
            .ConfigureAwait(false);
        // Changing the window briefly stops and restarts microWakeWord, so apply it last.
        await WriteNumberAsync(SlidingWindowEntity, tuning.SlidingWindow, cancellationToken)
            .ConfigureAwait(false);

        var started = Stopwatch.GetTimestamp();
        VoicePeWakeTuning actual;
        do
        {
            actual = await GetAsync(cancellationToken).ConfigureAwait(false);
            if (Matches(tuning, actual))
            {
                return actual;
            }

            await Task.Delay(ApplyPollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (Stopwatch.GetElapsedTime(started) < ApplyTimeout);

        throw new InvalidDataException(
            $"Voice PE did not confirm the requested wake tuning within {ApplyTimeout.TotalSeconds:F0} seconds. "
            + $"Requested {Describe(tuning)}; device reported {Describe(actual)}.");
    }

    public async Task<bool> GetBargeInAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient
            .GetAsync(new Uri(_baseUri, $"switch/{BargeInEntity}"), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("value", out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }
        if (root.TryGetProperty("state", out var state)
            && state.ValueKind == JsonValueKind.String)
        {
            return string.Equals(state.GetString(), "ON", StringComparison.OrdinalIgnoreCase);
        }

        throw new InvalidDataException("Voice PE returned no Joydex Audio Barge In state.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<double> ReadNumberAsync(string entity, CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(new Uri(_baseUri, $"number/{entity}"), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number))
        {
            throw new InvalidDataException($"Voice PE number '{entity}' returned no finite numeric value.");
        }

        return number;
    }

    private async Task WriteNumberAsync(
        string entity,
        double value,
        CancellationToken cancellationToken)
    {
        var formattedValue = value.ToString("0.00####", CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_baseUri, $"number/{entity}/set?value={formattedValue}"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static int ToInteger(double value, string entity)
    {
        var rounded = Math.Round(value);
        if (Math.Abs(value - rounded) > 0.001)
        {
            throw new InvalidDataException($"Voice PE number '{entity}' returned non-integral value {value}.");
        }

        return checked((int)rounded);
    }

    private static bool Matches(VoicePeWakeTuning expected, VoicePeWakeTuning actual) =>
        expected.MicrophoneGain == actual.MicrophoneGain
        && Math.Abs(expected.WakeProbabilityCutoff - actual.WakeProbabilityCutoff) <= 0.005
        && expected.SlidingWindow == actual.SlidingWindow
        && Math.Abs(expected.VadProbabilityCutoff - actual.VadProbabilityCutoff) <= 0.005;

    private static string Describe(VoicePeWakeTuning tuning) =>
        $"gain={tuning.MicrophoneGain}, wake={tuning.WakeProbabilityCutoff:F2}, "
        + $"window={tuning.SlidingWindow}, VAD={tuning.VadProbabilityCutoff:F2}";
}
