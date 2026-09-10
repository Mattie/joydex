using System.Text.Json;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class VoicePeLanAudioTransportTests
{
    [Fact]
    public void ProductionHybridAcceptsMicrophoneOnlyHello()
    {
        using var document = JsonDocument.Parse(
            """
            {"type":"hello","protocol":1,"mode":"uplink_only","uplink":{},"downlink":null}
            """);

        VoicePeLanAudioTransport.ValidateHello(document.RootElement, requireUplinkOnly: true);
    }

    [Theory]
    [InlineData("{\"type\":\"hello\",\"protocol\":1,\"uplink\":{},\"downlink\":{}}")]
    [InlineData("{\"type\":\"hello\",\"protocol\":1,\"mode\":\"uplink_only\",\"uplink\":{},\"downlink\":{}}")]
    [InlineData("{\"type\":\"hello\",\"protocol\":1,\"mode\":\"duplex\",\"uplink\":{},\"downlink\":null}")]
    public void ProductionHybridRejectsAnyHelloThatCanOwnRawSpeakerDownlink(string json)
    {
        using var document = JsonDocument.Parse(json);

        var error = Assert.Throws<InvalidDataException>(() =>
            VoicePeLanAudioTransport.ValidateHello(document.RootElement, requireUplinkOnly: true));

        Assert.Contains("microphone-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticTransportStillAcceptsLegacyDuplexHello()
    {
        using var document = JsonDocument.Parse(
            """
            {"type":"hello","protocol":1,"uplink":{},"downlink":{}}
            """);

        VoicePeLanAudioTransport.ValidateHello(document.RootElement, requireUplinkOnly: false);
    }
}
