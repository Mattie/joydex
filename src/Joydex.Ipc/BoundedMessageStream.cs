using System.Buffers.Binary;

namespace Joydex.Ipc;

/// <summary>
/// Validates each big-endian length-prefixed frame before its body reaches StreamJsonRpc.
/// Read and write framing have separate state because the pipe is full duplex.
/// </summary>
internal sealed class BoundedMessageStream(Stream inner, int maximumMessageBytes) : Stream
{
    private readonly byte[] _readHeader = new byte[sizeof(int)];
    private readonly byte[] _writeHeader = new byte[sizeof(int)];
    private int _readHeaderBytes;
    private int _readBodyBytesRemaining;
    private int _writeHeaderBytes;
    private int _writeBodyBytesRemaining;
    private bool _disposed;

    public override bool CanRead => !_disposed && inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed && inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wanted = GetReadCount(buffer.Length);
        var read = inner.Read(buffer[..wanted]);
        AdvanceReadState(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wanted = GetReadCount(buffer.Length);
        var read = await inner.ReadAsync(buffer[..wanted], cancellationToken).ConfigureAwait(false);
        AdvanceReadState(buffer.Span[..read]);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!buffer.IsEmpty)
        {
            var count = GetWriteCount(buffer.Length);
            var part = buffer[..count];
            AdvanceWriteState(part);
            inner.Write(part);
            buffer = buffer[count..];
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!buffer.IsEmpty)
        {
            var count = GetWriteCount(buffer.Length);
            var part = buffer[..count];
            AdvanceWriteState(part.Span);
            await inner.WriteAsync(part, cancellationToken).ConfigureAwait(false);
            buffer = buffer[count..];
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    private int GetReadCount(int requested)
    {
        if (requested <= 0)
        {
            return 0;
        }
        return _readHeaderBytes < sizeof(int)
            ? Math.Min(requested, sizeof(int) - _readHeaderBytes)
            : Math.Min(requested, _readBodyBytesRemaining);
    }

    private void AdvanceReadState(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }
        if (_readHeaderBytes < sizeof(int))
        {
            bytes.CopyTo(_readHeader.AsSpan(_readHeaderBytes));
            _readHeaderBytes += bytes.Length;
            if (_readHeaderBytes == sizeof(int))
            {
                _readBodyBytesRemaining = ValidateLength(_readHeader);
            }
            return;
        }

        _readBodyBytesRemaining -= bytes.Length;
        if (_readBodyBytesRemaining == 0)
        {
            _readHeaderBytes = 0;
        }
    }

    private int GetWriteCount(int requested)
    {
        if (_writeHeaderBytes < sizeof(int))
        {
            return Math.Min(requested, sizeof(int) - _writeHeaderBytes);
        }
        return Math.Min(requested, _writeBodyBytesRemaining);
    }

    private void AdvanceWriteState(ReadOnlySpan<byte> bytes)
    {
        if (_writeHeaderBytes < sizeof(int))
        {
            bytes.CopyTo(_writeHeader.AsSpan(_writeHeaderBytes));
            _writeHeaderBytes += bytes.Length;
            if (_writeHeaderBytes == sizeof(int))
            {
                _writeBodyBytesRemaining = ValidateLength(_writeHeader);
            }
            return;
        }

        _writeBodyBytesRemaining -= bytes.Length;
        if (_writeBodyBytesRemaining == 0)
        {
            _writeHeaderBytes = 0;
        }
    }

    private int ValidateLength(ReadOnlySpan<byte> header)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > maximumMessageBytes)
        {
            throw new RuntimeIpcMessageTooLargeException(length, maximumMessageBytes);
        }
        return length;
    }
}
