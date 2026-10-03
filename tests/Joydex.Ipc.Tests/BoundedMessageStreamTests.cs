using System.Buffers.Binary;

namespace Joydex.Ipc.Tests;

public sealed class BoundedMessageStreamTests
{
    [Fact]
    public async Task OversizedFrameIsRejectedAfterReadingOnlyItsHeader()
    {
        var bytes = new byte[128];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 101);
        await using var source = new CountingMemoryStream(bytes);
        await using var bounded = new BoundedMessageStream(source, maximumMessageBytes: 100);

        var error = await Assert.ThrowsAsync<RuntimeIpcMessageTooLargeException>(async () =>
            await bounded.ReadAsync(new byte[4096]));

        Assert.Equal(101, error.ActualBytes);
        Assert.Equal(sizeof(int), source.BytesRead);
    }

    [Fact]
    public async Task OversizedOutboundFrameIsRejectedBeforeWritingItsHeader()
    {
        await using var destination = new MemoryStream();
        await using var bounded = new BoundedMessageStream(destination, maximumMessageBytes: 100);
        var frame = Frame(new byte[101]);

        var error = await Assert.ThrowsAsync<RuntimeIpcMessageTooLargeException>(async () =>
            await bounded.WriteAsync(frame));

        Assert.Equal(101, error.ActualBytes);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ConsecutiveFramesAreBoundAndPreserved()
    {
        var bytes = Frame([1, 2, 3]).Concat(Frame([4, 5])).ToArray();
        await using var bounded = new BoundedMessageStream(
            new MemoryStream(bytes),
            maximumMessageBytes: 3);
        var observed = new List<byte>();
        var buffer = new byte[16];

        int read;
        while ((read = await bounded.ReadAsync(buffer)) > 0)
        {
            observed.AddRange(buffer[..read]);
        }

        Assert.Equal(bytes, observed);
    }

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        return frame;
    }

    private sealed class CountingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var pending = base.ReadAsync(buffer, cancellationToken);
            if (pending.IsCompletedSuccessfully)
            {
                var read = pending.Result;
                BytesRead += read;
                return new ValueTask<int>(read);
            }
            return AwaitReadAsync(pending);
        }

        private async ValueTask<int> AwaitReadAsync(ValueTask<int> pending)
        {
            var read = await pending;
            BytesRead += read;
            return read;
        }
    }
}
