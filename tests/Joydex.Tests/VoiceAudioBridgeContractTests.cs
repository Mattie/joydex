using Joydex.Core.Voice;
using Joydex.App;

namespace Joydex.Tests;

public sealed class VoiceAudioBridgeContractTests
{
    [Theory]
    [InlineData(16_000, 640)]
    [InlineData(24_000, 960)]
    [InlineData(48_000, 1_920)]
    public void CalculatesTwentyMillisecondMonoPcmFrames(int sampleRate, int expectedBytes)
    {
        var format = new VoicePcmFormat(sampleRate, 1);

        Assert.Equal(expectedBytes, format.GetByteCount(TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void RejectsDurationsThatSplitASampleFrame()
    {
        var format = new VoicePcmFormat(24_000, 1);

        Assert.Throws<ArgumentException>(() => format.GetByteCount(TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void AcceptsAlignedPcmPayloads()
    {
        var format = new VoicePcmFormat(24_000, 1);
        var frame = new VoicePcmFrame(7, new byte[format.GetByteCount(TimeSpan.FromMilliseconds(20))]);

        frame.Validate(format);
    }

    [Fact]
    public void RejectsPartialPcmSampleFrames()
    {
        var format = new VoicePcmFormat(24_000, 2);
        var frame = new VoicePcmFrame(0, new byte[3]);

        Assert.Throws<ArgumentException>(() => frame.Validate(format));
    }

    [Fact]
    public void AppliesConversationMicrophoneGainWithPcm16ClippingProtection()
    {
        var inputSamples = new short[] { 1_000, -1_000, 10_000, -10_000, 0 };
        var input = new byte[inputSamples.Length * sizeof(short)];
        Buffer.BlockCopy(inputSamples, 0, input, 0, input.Length);

        var amplified = WebView2VoiceDuplexAudioSession.ApplyMicrophoneGain(input, gain: 8);
        var outputSamples = new short[inputSamples.Length];
        Buffer.BlockCopy(amplified, 0, outputSamples, 0, amplified.Length);

        Assert.Equal(new short[] { 8_000, -8_000, short.MaxValue, short.MinValue, 0 }, outputSamples);
    }

    [Fact]
    public void ConversationMicrophoneGainRejectsInvalidPcm()
    {
        Assert.Throws<ArgumentException>(
            () => WebView2VoiceDuplexAudioSession.ApplyMicrophoneGain(new byte[3], gain: 8));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WebView2VoiceDuplexAudioSession.ApplyMicrophoneGain(new byte[2], gain: 0));
    }

    [Fact]
    public void AppliesConversationSpeakerGainWithPcm16ClippingProtection()
    {
        var inputSamples = new short[] { 6_000, -6_000, 20_000, -20_000, 0 };
        var input = new byte[inputSamples.Length * sizeof(short)];
        Buffer.BlockCopy(inputSamples, 0, input, 0, input.Length);

        var amplified = WebView2VoiceDuplexAudioSession.ApplyPcm16Gain(input, gain: 2);
        var outputSamples = new short[inputSamples.Length];
        Buffer.BlockCopy(amplified, 0, outputSamples, 0, amplified.Length);

        Assert.Equal(new short[] { 12_000, -12_000, short.MaxValue, short.MinValue, 0 }, outputSamples);
    }

    [Fact]
    public void BoundsWebRtcSpeakerOutputBeforeThePlayoutQueue()
    {
        var channel = WebView2VoiceDuplexAudioSession.CreateSpeakerOutputChannel();

        for (var index = 0; index < WebView2VoiceDuplexAudioSession.MaximumBufferedSpeakerOutputs; index++)
        {
            Assert.True(channel.Writer.TryWrite(VoiceSpeakerOutput.Ended));
        }

        Assert.False(channel.Writer.TryWrite(VoiceSpeakerOutput.Ended));
    }

    [Fact]
    public void BuffersTheBoundedDeviceStartupWindowDuringContinuousWebRtcCapture()
    {
        const int framesPerSecond = 50;
        const int minimumStartupWindowSeconds = 40;

        Assert.True(
            WebView2VoiceDuplexAudioSession.MaximumBufferedSpeakerOutputs
            >= framesPerSecond * minimumStartupWindowSeconds);
    }

    [Fact]
    public void WritesAStandardMonoPcm16DiagnosticWave()
    {
        var pcm = new byte[] { 0x34, 0x12, 0xcc, 0xed };

        var wave = WebView2VoiceDuplexAudioSession.CreatePcm16MonoWave(pcm, 24_000);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wave, 12, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wave, 36, 4));
        Assert.Equal(24_000, BitConverter.ToInt32(wave, 24));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wave, 40));
        Assert.Equal(pcm, wave[44..]);
    }

    [Fact]
    public void PackagedSpeakerWorkletHasNoTurnActivityGateAndAcknowledgesFlush()
    {
        var assetDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Voice");
        var processor = File.ReadAllText(Path.Combine(assetDirectory, "duplex-processor.js"));
        var page = File.ReadAllText(Path.Combine(assetDirectory, "index.html"));

        Assert.Contains("this.pendingBoundaries = [];", processor);
        Assert.Contains("if (!input) return true;", processor);
        Assert.DoesNotContain("this.outputActive", processor);
        Assert.Contains("type: 'capture-flushed'", processor);
        Assert.DoesNotContain("type: 'output-active'", page);
        Assert.Contains("await flushSpeakerCapture();", page);
        Assert.Contains("post({ type: 'stopped' });", page);
    }

    [Fact]
    public void KeepsRawDecodedSpeakerPcmIndependentOfPlayoutGain()
    {
        var samples = new short[480];
        samples[0] = 20_000;
        samples[1] = -20_000;
        var input = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, input, 0, input.Length);

        var (decoded, playout) = WebView2VoiceDuplexAudioSession.PrepareSpeakerPcm(
            Convert.ToBase64String(input),
            gain: 2);

        Assert.Equal(input, decoded);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(playout, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(playout, sizeof(short)));
    }

    [Fact]
    public void RejectsDecodedSpeakerFramesWithTheWrongDuration()
    {
        var encoded = Convert.ToBase64String(new byte[2]);

        Assert.Throws<InvalidDataException>(
            () => WebView2VoiceDuplexAudioSession.PrepareSpeakerPcm(encoded, gain: 2));
    }

    [Fact]
    public void MergesIncrementalUserTranscriptsForLocalVoiceControls()
    {
        var transcript = WebView2VoiceDuplexAudioSession.MergeUserTranscript(string.Empty, "Computer");
        transcript = WebView2VoiceDuplexAudioSession.MergeUserTranscript(transcript, "hang up please");

        Assert.Equal("Computer hang up please", transcript);
    }

    [Fact]
    public void ReplacesIncrementalTextWithTheCompletedTranscriptWithoutDuplicatingIt()
    {
        var transcript = WebView2VoiceDuplexAudioSession.MergeUserTranscript("Computer", "Computer, hang up please");
        transcript = WebView2VoiceDuplexAudioSession.MergeUserTranscript(transcript, "Computer, hang up please");

        Assert.Equal("Computer, hang up please", transcript);
    }

    [Theory]
    [InlineData("hang up")]
    [InlineData("Computer, hang up please.")]
    [InlineData("Could you cancel the voice chat?")]
    [InlineData("end this conversation")]
    [InlineData("please stop the voice chat now")]
    [InlineData("Okay, I think we're done, you can hang up now.")]
    [InlineData("That is all; go ahead and end this conversation, please.")]
    [InlineData("Thanks, please cancel this voice chat for me.")]
    [InlineData("goodbye")]
    [InlineData("Computer, bye!")]
    [InlineData("okay, bye bye now")]
    [InlineData("please shut up now")]
    [InlineData("end")]
    [InlineData("Computer, die")]
    public void RecognizesExplicitSpokenHangupCommands(string transcript)
    {
        Assert.True(WebView2VoiceDuplexAudioSession.IsSpokenHangupCommand(transcript));
    }

    [Theory]
    [InlineData("How do I hang up a voice chat?")]
    [InlineData("Don't hang up")]
    [InlineData("Stop talking and tell me the capital of Maine")]
    [InlineData("cancel my timer")]
    [InlineData("goodbye for now")]
    [InlineData("Tell me how the movie will end")]
    [InlineData("When did Socrates die?")]
    [InlineData("How do I say goodbye in Spanish?")]
    [InlineData("What does shut up mean?")]
    public void DoesNotTreatConversationTextAsAHangupCommand(string transcript)
    {
        Assert.False(WebView2VoiceDuplexAudioSession.IsSpokenHangupCommand(transcript));
    }
}
