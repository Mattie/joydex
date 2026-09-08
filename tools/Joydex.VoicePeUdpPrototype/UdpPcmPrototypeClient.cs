using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Joydex.VoicePeUdpPrototype;

internal sealed record UdpPcmClientSnapshot(
    uint SessionId,
    long DatagramsSent,
    long SpeakerFramesSent,
    long MicrophoneFramesReceived,
    long MicrophoneMissingPackets,
    long MicrophoneLatePackets,
    long InvalidPackets,
    long PongsReceived,
    bool Ready,
    bool Closed);

/// <summary>
/// Host half of the throwaway UDPPCM transport, including handshake and liveness state.
/// </summary>
internal sealed class UdpPcmPrototypeClient : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromMilliseconds(3500);

    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<UdpPcmPacket> _microphonePackets = Channel.CreateBounded<UdpPcmPacket>(
        new BoundedChannelOptions(40)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly uint _sessionId = checked((uint) RandomNumberGenerator.GetInt32(1, int.MaxValue));
    private Task? _receiveTask;
    private Task? _keepaliveTask;
    private long _lastInboundTimestamp;
    private uint _speakerSequence;
    private uint _pingSequence;
    private uint _expectedMicrophoneSequence;
    private int _microphoneSequenceStarted;
    private long _datagramsSent;
    private long _speakerFramesSent;
    private long _microphoneFramesReceived;
    private long _microphoneMissingPackets;
    private long _microphoneLatePackets;
    private long _invalidPackets;
    private long _pongsReceived;
    private int _opened;
    private int _disposed;

    public Task Completion => _completion.Task;

    public async Task OpenAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _opened, 1, 0) != 0)
        {
            throw new InvalidOperationException("The UDPPCM prototype client is already open.");
        }

        _udp.Connect(new IPEndPoint(address, port));
        Volatile.Write(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());
        _receiveTask = ReceiveLoopAsync(_lifetime.Token);
        _keepaliveTask = KeepaliveLoopAsync(_lifetime.Token);

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        handshake.CancelAfter(HandshakeTimeout);
        while (!_ready.Task.IsCompleted)
        {
            await SendPacketAsync(UdpPcmPacketType.Open, 0, default, handshake.Token).ConfigureAwait(false);
            var completed = await Task.WhenAny(
                    _ready.Task,
                    Task.Delay(TimeSpan.FromMilliseconds(250), handshake.Token))
                .ConfigureAwait(false);
            if (completed == _ready.Task)
            {
                break;
            }
        }
        await _ready.Task.WaitAsync(handshake.Token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<UdpPcmPacket> ReadMicrophonePacketsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var packet in _microphonePackets.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return packet;
        }
    }

    public async ValueTask SendSpeakerFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length != UdpPcmProtocol.SpeakerPayloadBytes)
        {
            throw new ArgumentException(
                $"UDPPCM speaker frames must contain {UdpPcmProtocol.SpeakerPayloadBytes} bytes.",
                nameof(payload));
        }
        if (!_ready.Task.IsCompletedSuccessfully)
        {
            throw new IOException("The UDPPCM prototype session is not ready.");
        }

        await SendPacketAsync(
                UdpPcmPacketType.Speaker,
                _speakerSequence++,
                payload,
                cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Increment(ref _speakerFramesSent);
    }

    public ValueTask NotifySpeakerPlaybackEndedAsync(CancellationToken cancellationToken) =>
        SendPacketAsync(UdpPcmPacketType.PlaybackEnd, _speakerSequence, default, cancellationToken);

    public UdpPcmClientSnapshot Snapshot() => new(
        _sessionId,
        Interlocked.Read(ref _datagramsSent),
        Interlocked.Read(ref _speakerFramesSent),
        Interlocked.Read(ref _microphoneFramesReceived),
        Interlocked.Read(ref _microphoneMissingPackets),
        Interlocked.Read(ref _microphoneLatePackets),
        Interlocked.Read(ref _invalidPackets),
        Interlocked.Read(ref _pongsReceived),
        _ready.Task.IsCompletedSuccessfully,
        _closed.Task.IsCompletedSuccessfully);

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _opened, 0) == 0)
        {
            return;
        }

        try
        {
            for (var attempt = 0; attempt < 3 && !_closed.Task.IsCompleted; attempt++)
            {
                await SendPacketAsync(UdpPcmPacketType.Close, 0, default, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _closed.Task.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
        }
        finally
        {
            _lifetime.Cancel();
            await ObserveAsync(_receiveTask).ConfigureAwait(false);
            await ObserveAsync(_keepaliveTask).ConfigureAwait(false);
            _microphonePackets.Writer.TryComplete();
            _completion.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _udp.Dispose();
            _sendGate.Dispose();
            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!UdpPcmProtocol.TryDecode(result.Buffer, out var packet) || packet.SessionId != _sessionId)
                {
                    Interlocked.Increment(ref _invalidPackets);
                    continue;
                }
                switch (packet.Type)
                {
                    case UdpPcmPacketType.Ready when packet.Payload.Length == 0:
                        Volatile.Write(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());
                        _ready.TrySetResult();
                        break;
                    case UdpPcmPacketType.Closed when packet.Payload.Length == 0:
                        Volatile.Write(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());
                        _closed.TrySetResult();
                        break;
                    case UdpPcmPacketType.Pong when packet.Payload.Length == 0:
                        Volatile.Write(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());
                        Interlocked.Increment(ref _pongsReceived);
                        break;
                    case UdpPcmPacketType.Microphone
                        when packet.Payload.Length == UdpPcmProtocol.MicrophonePayloadBytes:
                        Volatile.Write(ref _lastInboundTimestamp, Stopwatch.GetTimestamp());
                        HandleMicrophonePacket(packet);
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
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void HandleMicrophonePacket(UdpPcmPacket packet)
    {
        if (Interlocked.CompareExchange(ref _microphoneSequenceStarted, 1, 0) == 0)
        {
            _expectedMicrophoneSequence = 0;
        }
        if (packet.Sequence < _expectedMicrophoneSequence)
        {
            Interlocked.Increment(ref _microphoneLatePackets);
            return;
        }
        if (packet.Sequence > _expectedMicrophoneSequence)
        {
            Interlocked.Add(ref _microphoneMissingPackets, packet.Sequence - _expectedMicrophoneSequence);
        }
        _expectedMicrophoneSequence = packet.Sequence + 1;
        Interlocked.Increment(ref _microphoneFramesReceived);
        _microphonePackets.Writer.TryWrite(packet);
    }

    private async Task KeepaliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(KeepaliveInterval, cancellationToken).ConfigureAwait(false);
                await SendPacketAsync(UdpPcmPacketType.Ping, _pingSequence++, default, cancellationToken)
                    .ConfigureAwait(false);
                var lastInbound = Volatile.Read(ref _lastInboundTimestamp);
                if (Stopwatch.GetElapsedTime(lastInbound) > PeerTimeout)
                {
                    throw new IOException("The UDPPCM prototype peer stopped answering keepalives.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private async ValueTask SendPacketAsync(
        UdpPcmPacketType type,
        uint sequence,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_completion.Task.IsFaulted)
        {
            await _completion.Task.ConfigureAwait(false);
        }
        var datagram = UdpPcmProtocol.Encode(type, _sessionId, sequence, payload.Span);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sent = await _udp.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
            if (sent != datagram.Length)
            {
                throw new IOException($"UDPPCM sent {sent} of {datagram.Length} bytes.");
            }
            Interlocked.Increment(ref _datagramsSent);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void Fail(Exception exception)
    {
        _ready.TrySetException(exception);
        _microphonePackets.Writer.TryComplete(exception);
        _completion.TrySetException(exception);
        _lifetime.Cancel();
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
