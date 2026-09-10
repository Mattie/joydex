using System.Globalization;
using System.Net;
using System.Text;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class VoicePeWakeTuningTests
{
    [Fact]
    public async Task ReadsAllDeviceOwnedWakeValues()
    {
        var handler = new WakeTuningHandler(new VoicePeWakeTuning(4, 0.35, 5, 0.10));
        using var httpClient = new HttpClient(handler);
        using var client = new EspHomeVoicePeTuningClient(
            httpClient,
            new Uri("http://voice-pe.local/"));

        var tuning = await client.GetAsync();

        Assert.Equal(new VoicePeWakeTuning(4, 0.35, 5, 0.10), tuning);
        Assert.Equal(
            [
                "/number/joydex_vad_probability_cutoff",
                "/number/joydex_wake_microphone_gain",
                "/number/joydex_wake_probability_cutoff",
                "/number/joydex_wake_sliding_window",
            ],
            handler.Requests.Select(request => request.AbsolutePath).Order().ToArray());
    }

    [Fact]
    public async Task AppliesAllValuesAndReadsThemBack()
    {
        var handler = new WakeTuningHandler(VoicePeWakeTuning.Default);
        using var httpClient = new HttpClient(handler);
        using var client = new EspHomeVoicePeTuningClient(
            httpClient,
            new Uri("http://voice-pe.local/"));
        var requested = new VoicePeWakeTuning(6, 0.42, 4, 0.08);

        var confirmed = await client.SetAsync(requested);

        Assert.Equal(requested, confirmed);
        Assert.Equal(
            [
                "joydex_wake_microphone_gain",
                "joydex_wake_probability_cutoff",
                "joydex_vad_probability_cutoff",
                "joydex_wake_sliding_window",
            ],
            handler.WrittenEntities);
    }

    [Fact]
    public async Task ReadsPersistedBargeInState()
    {
        var handler = new WakeTuningHandler(VoicePeWakeTuning.Default, bargeIn: true);
        using var httpClient = new HttpClient(handler);
        using var client = new EspHomeVoicePeTuningClient(
            httpClient,
            new Uri("http://voice-pe.local/"));

        Assert.True(await client.GetBargeInAsync());
        Assert.Contains(
            handler.Requests,
            request => request.AbsolutePath == "/switch/joydex_audio_barge_in");
    }

    [Fact]
    public async Task RejectsOutOfRangeValuesBeforeContactingTheDevice()
    {
        var handler = new WakeTuningHandler(VoicePeWakeTuning.Default);
        using var httpClient = new HttpClient(handler);
        using var client = new EspHomeVoicePeTuningClient(
            httpClient,
            new Uri("http://voice-pe.local/"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.SetAsync(VoicePeWakeTuning.Default with { MicrophoneGain = 9 }));

        Assert.Empty(handler.Requests);
    }

    private sealed class WakeTuningHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, double> _values;
        private readonly bool _bargeIn;

        public WakeTuningHandler(VoicePeWakeTuning initial, bool bargeIn = false)
        {
            _bargeIn = bargeIn;
            _values = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["joydex_wake_microphone_gain"] = initial.MicrophoneGain,
                ["joydex_wake_probability_cutoff"] = initial.WakeProbabilityCutoff,
                ["joydex_wake_sliding_window"] = initial.SlidingWindow,
                ["joydex_vad_probability_cutoff"] = initial.VadProbabilityCutoff,
            };
        }

        public List<Uri> Requests { get; } = [];
        public List<string> WrittenEntities { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.RequestUri);
            var uri = request.RequestUri;
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments[0] == "switch")
            {
                Assert.Equal("joydex_audio_barge_in", segments[1]);
                Requests.Add(uri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"state":"{{(_bargeIn ? "ON" : "OFF")}}","value":{{_bargeIn.ToString().ToLowerInvariant()}}}""",
                        Encoding.UTF8,
                        "application/json"),
                    RequestMessage = request,
                });
            }

            Assert.Equal("number", segments[0]);
            var entity = segments[1];
            double value;
            lock (_gate)
            {
                Requests.Add(uri);
                if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal("set", segments[2]);
                    var rawValue = uri.Query["?value=".Length..];
                    value = double.Parse(rawValue, CultureInfo.InvariantCulture);
                    _values[entity] = value;
                    WrittenEntities.Add(entity);
                }
                else
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    value = _values[entity];
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"value":{{value.ToString(CultureInfo.InvariantCulture)}}}""",
                    Encoding.UTF8,
                    "application/json"),
                RequestMessage = request,
            });
        }
    }
}
