using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Joydex.VoicePeUdpPrototype;

internal sealed record UdpPcmDeviceSnapshot(
    long Opens,
    long SpeakerFramesReceived,
    long SpeakerMissingPackets,
    long SpeakerLatePackets,
    long MicrophoneFramesSent,
    long InvalidPackets,
    long PingsReceived,
    bool SessionOpen);

/// <summary>
/// In-memory-state UDP peer used only to exercise the prototype contract before a device flash.
/// </summary>
internal sealed class UdpPcmLoopbackDevice : IAsyncDisposable
{
    private const int CloseAcknowledgementBurst = 3;
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _receiveTask;
    private IPEndPoint? _peer;
    private uint _sessionId;
    private uint _expectedSpeakerSequence;
    private uint _microphoneSequence;
    private int _speakerSequenceStarted;
    private long _opens;
    private long _speakerFramesReceived;
    private long _speakerMissingPackets;
    private long _speakerLatePackets;
    private long _microphoneFramesSent;
    private long _invalidPackets;
    private long _pingsReceived;
    private int _sessionOpen;
    private int _disposed;

    public UdpPcmLoopbackDevice()
    {
        Endpoint = (IPEndPoint) (_udp.Client.LocalEndPoint
            ?? throw new InvalidOperationException("Loopback UDP socket did not bind."));
        _receiveTask = ReceiveLoopAsync(_lifetime.Token);
    }

    public IPEndPoint Endpoint { get; }

    public UdpPcmDeviceSnapshot Snapshot() => new(
        Interlocked.Read(ref _opens),
        Interlocked.Read(ref _speakerFramesReceived),
        Interlocked.Read(ref _speakerMissingPackets),
        Interlocked.Read(ref _speakerLatePackets),
        Interlocked.Read(ref _microphoneFramesSent),
        Interlocked.Read(ref _invalidPackets),
        Interlocked.Read(ref _pingsReceived),
        Volatile.Read(ref _sessionOpen) != 0);

    public async Task EmitMicrophoneAsync(int frameCount, CancellationToken cancellationToken)
    {
        var payload = new byte[UdpPcmProtocol.MicrophonePayloadBytes];
        var clock = Stopwatch.StartNew();
        using var timerResolution = TimerResolutionLease.Acquire();
        for (var frame = 0; frame < frameCount; frame++)
        {
            await WaitUntilAsync(clock, frame * 20.0, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _sessionOpen) == 0)
            {
                throw new IOException("The loopback UDPPCM session closed during microphone output.");
            }
            await SendAsync(UdpPcmPacketType.Microphone, _microphoneSequence++, payload, cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _microphoneFramesSent);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _lifetime.Cancel();
        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        _udp.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!UdpPcmProtocol.TryDecode(result.Buffer, out var packet))
                {
                    Interlocked.Increment(ref _invalidPackets);
                    continue;
                }

                if (packet.Type == UdpPcmPacketType.Open)
                {
                    if (packet.Payload.Length != 0)
                    {
                        Interlocked.Increment(ref _invalidPackets);
                        continue;
                    }
                    if (Volatile.Read(ref _sessionOpen) == 0 || packet.SessionId != _sessionId)
                    {
                        _peer = result.RemoteEndPoint;
                        _sessionId = packet.SessionId;
                        _expectedSpeakerSequence = 0;
                        _microphoneSequence = 0;
                        Interlocked.Exchange(ref _speakerSequenceStarted, 0);
                        Interlocked.Exchange(ref _sessionOpen, 1);
                        Interlocked.Increment(ref _opens);
                    }
                    await SendAsync(UdpPcmPacketType.Ready, 0, default, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (Volatile.Read(ref _sessionOpen) == 0
                    || packet.SessionId != _sessionId
                    || _peer is null
                    || !result.RemoteEndPoint.Equals(_peer))
                {
                    Interlocked.Increment(ref _invalidPackets);
                    continue;
                }

                switch (packet.Type)
                {
                    case UdpPcmPacketType.Close when packet.Payload.Length == 0:
                        for (uint acknowledgement = 0; acknowledgement < CloseAcknowledgementBurst; acknowledgement++)
                        {
                            await SendAsync(UdpPcmPacketType.Closed, acknowledgement, default, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        Interlocked.Exchange(ref _sessionOpen, 0);
                        break;
                    case UdpPcmPacketType.Ping when packet.Payload.Length == 0:
                        Interlocked.Increment(ref _pingsReceived);
                        await SendAsync(UdpPcmPacketType.Pong, packet.Sequence, default, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case UdpPcmPacketType.Speaker
                        when packet.Payload.Length == UdpPcmProtocol.SpeakerPayloadBytes:
                        HandleSpeaker(packet.Sequence);
                        break;
                    case UdpPcmPacketType.PlaybackEnd when packet.Payload.Length == 0:
                        break;
                    default:
                        Interlocked.Increment(ref _invalidPackets);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void HandleSpeaker(uint sequence)
    {
        if (Interlocked.CompareExchange(ref _speakerSequenceStarted, 1, 0) == 0)
        {
            _expectedSpeakerSequence = 0;
        }
        if (sequence < _expectedSpeakerSequence)
        {
            Interlocked.Increment(ref _speakerLatePackets);
            return;
        }
        if (sequence > _expectedSpeakerSequence)
        {
            Interlocked.Add(ref _speakerMissingPackets, sequence - _expectedSpeakerSequence);
        }
        _expectedSpeakerSequence = sequence + 1;
        Interlocked.Increment(ref _speakerFramesReceived);
    }

    private async ValueTask SendAsync(
        UdpPcmPacketType type,
        uint sequence,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var peer = _peer ?? throw new IOException("The loopback UDPPCM host is not open.");
        var datagram = UdpPcmProtocol.Encode(type, _sessionId, sequence, payload.Span);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sent = await _udp.SendAsync(datagram, peer, cancellationToken).ConfigureAwait(false);
            if (sent != datagram.Length)
            {
                throw new IOException($"Loopback UDPPCM sent {sent} of {datagram.Length} bytes.");
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task WaitUntilAsync(Stopwatch clock, double targetMilliseconds, CancellationToken token)
    {
        while (true)
        {
            var remaining = targetMilliseconds - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(remaining), token).ConfigureAwait(false);
        }
    }
}
