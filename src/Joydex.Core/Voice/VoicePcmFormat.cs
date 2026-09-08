namespace Joydex.Core.Voice;

/// <summary>
/// Describes interleaved signed 16-bit little-endian PCM at one fixed sample rate.
/// </summary>
public sealed record VoicePcmFormat
{
    public const int BytesPerSample = 2;
    public const int MinimumSampleRate = 8_000;
    public const int MaximumSampleRate = 192_000;
    public const int MinimumChannelCount = 1;
    public const int MaximumChannelCount = 8;

    public VoicePcmFormat(int sampleRate, int channelCount)
    {
        if (sampleRate is < MinimumSampleRate or > MaximumSampleRate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                $"PCM sample rate must be from {MinimumSampleRate} through {MaximumSampleRate} Hz.");
        }

        if (channelCount is < MinimumChannelCount or > MaximumChannelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelCount),
                $"PCM channel count must be from {MinimumChannelCount} through {MaximumChannelCount}.");
        }

        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }

    public int SampleRate { get; }

    public int ChannelCount { get; }

    public int BytesPerSampleFrame => BytesPerSample * ChannelCount;

    /// <summary>
    /// Returns the exact payload size for a duration that lands on a complete PCM sample frame.
    /// </summary>
    public int GetByteCount(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "PCM frame duration must be positive.");
        }

        var sampleNumerator = checked((long)SampleRate * duration.Ticks);
        if (sampleNumerator % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException(
                "PCM frame duration must contain a whole number of sample frames.",
                nameof(duration));
        }

        var sampleFrames = sampleNumerator / TimeSpan.TicksPerSecond;
        return checked((int)(sampleFrames * BytesPerSampleFrame));
    }
}

/// <summary>
/// One ordered PCM payload on either side of a full-duplex Voice Session.
/// </summary>
public readonly record struct VoicePcmFrame(long SequenceNumber, ReadOnlyMemory<byte> Payload)
{
    public void Validate(VoicePcmFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (SequenceNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SequenceNumber));
        }

        if (Payload.IsEmpty || Payload.Length % format.BytesPerSampleFrame != 0)
        {
            throw new ArgumentException(
                "PCM payload must contain one or more complete sample frames.",
                nameof(Payload));
        }
    }
}
