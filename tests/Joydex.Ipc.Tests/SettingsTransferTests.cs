using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;

namespace Joydex.Ipc.Tests;

public sealed class SettingsTransferTests
{
    [Fact]
    public void StoreRequiresSequentialChunksAndRejectsReplay()
    {
        using var store = new SettingsTransferStore();
        var payload = RandomNumberGenerator.GetBytes(SettingsTransferProtocol.ChunkBytes + 17);
        var reference = store.Add(SettingsTransferSerializer.FromBytes(payload));

        Assert.Throws<InvalidOperationException>(() => store.Read(reference.TransferId, 1));
        var first = store.Read(reference.TransferId, 0);
        Assert.False(first.Complete);
        Assert.Equal(SettingsTransferProtocol.ChunkBytes, first.Data.Length);
        Assert.Throws<InvalidOperationException>(() => store.Read(reference.TransferId, 0));

        var final = store.Read(reference.TransferId, first.Data.Length);
        Assert.True(final.Complete);
        Assert.Equal(17, final.Data.Length);
        Assert.Equal(payload, first.Data.Concat(final.Data).ToArray());
        Assert.Throws<InvalidOperationException>(() => store.Read(reference.TransferId, payload.Length));
    }

    [Fact]
    public void StoreBoundsConcurrentTransferCount()
    {
        using var store = new SettingsTransferStore();
        for (var index = 0; index < SettingsTransferProtocol.MaximumConcurrentTransfers; index++)
        {
            _ = store.Add(SettingsTransferSerializer.FromBytes([checked((byte)index)]));
        }

        Assert.Throws<InvalidOperationException>(() =>
            store.Add(SettingsTransferSerializer.FromBytes([42])));
    }

    [Fact]
    public async Task StoreEnforcesTransferCountUnderConcurrentPublishers()
    {
        using var store = new SettingsTransferStore();
        var references = new ConcurrentBag<SettingsTransferReference>();
        var failures = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            try
            {
                references.Add(store.Add(SettingsTransferSerializer.FromBytes([checked((byte)index)])));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref failures);
            }
        })));

        Assert.Equal(SettingsTransferProtocol.MaximumConcurrentTransfers, references.Count);
        Assert.Equal(12, failures);
    }

    [Fact]
    public void PublishingPurgesExpiredUnreadTransfersAndReleasesSlots()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var store = new SettingsTransferStore(time);
        for (var index = 0; index < SettingsTransferProtocol.MaximumConcurrentTransfers; index++)
        {
            _ = store.Add(SettingsTransferSerializer.FromBytes([checked((byte)index)]));
        }

        time.Advance(SettingsTransferProtocol.TransferLifetime + TimeSpan.FromSeconds(1));
        var replacement = store.Add(SettingsTransferSerializer.FromBytes([42]));

        Assert.Equal([42], store.Read(replacement.TransferId, 0).Data);
    }

    [Fact]
    public void ReferencesAreBoundToTheirConnectionStore()
    {
        using var owner = new SettingsTransferStore();
        using var otherConnection = new SettingsTransferStore();
        var reference = owner.Add(SettingsTransferSerializer.FromBytes([1, 2, 3]));

        Assert.Throws<InvalidOperationException>(() =>
            otherConnection.Read(reference.TransferId, 0));
        Assert.Equal([1, 2, 3], owner.Read(reference.TransferId, 0).Data);
    }

    [Fact]
    public async Task ReceiverValidatesHashAndMakesReferenceOneUse()
    {
        var value = new SampleValue("settings", 42);
        var payload = SerializeBytes(value);
        var reference = Reference(payload);
        using var receiver = ReceiverFor(reference, payload);

        Assert.Equal(value, await receiver.ReceiveAsync<SampleValue>(reference, CancellationToken.None));
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receiver.ReceiveAsync<SampleValue>(reference, CancellationToken.None));
        Assert.Contains("already used", replay.Message, StringComparison.OrdinalIgnoreCase);

        var badHash = reference with { TransferId = Guid.NewGuid().ToString("N"), Sha256 = new string('0', 64) };
        using var badHashReceiver = ReceiverFor(badHash, payload);
        var hashError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            badHashReceiver.ReceiveAsync<SampleValue>(badHash, CancellationToken.None));
        Assert.Contains("SHA-256", hashError.Message, StringComparison.OrdinalIgnoreCase);

        var uppercaseHash = Reference(payload) with
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(payload)),
        };
        using var uppercaseReceiver = ReceiverFor(uppercaseHash, payload);
        Assert.Equal(
            value,
            await uppercaseReceiver.ReceiveAsync<SampleValue>(uppercaseHash, CancellationToken.None));
    }

    [Fact]
    public async Task ReceiverRejectsWrongLengthOrderAndChunkSize()
    {
        var payload = SerializeBytes(new SampleValue("settings", 42));

        var wrongLength = Reference(payload) with { Length = payload.Length + 1 };
        using (var receiver = ReceiverFor(wrongLength, payload))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                receiver.ReceiveAsync<SampleValue>(wrongLength, CancellationToken.None));
        }

        var wrongOrder = Reference(payload);
        using (var receiver = new SettingsTransferReceiver((id, offset, _) => Task.FromResult(
                   new SettingsTransferChunk(id, offset + 1, payload, Complete: true))))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                receiver.ReceiveAsync<SampleValue>(wrongOrder, CancellationToken.None));
        }

        var oversizedPayload = RandomNumberGenerator.GetBytes(SettingsTransferProtocol.ChunkBytes + 1);
        var oversizedChunk = Reference(oversizedPayload);
        using (var receiver = new SettingsTransferReceiver((id, offset, _) => Task.FromResult(
                   new SettingsTransferChunk(id, offset, oversizedPayload, Complete: true))))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                receiver.ReceiveAsync<byte[]>(oversizedChunk, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ReceiverRejectsTrailingJsonBeforeReturningValue()
    {
        var value = SerializeBytes(new SampleValue("settings", 42));
        var trailing = value.Concat(Encoding.UTF8.GetBytes(" true")).ToArray();
        var reference = Reference(trailing);
        using var receiver = ReceiverFor(reference, trailing);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            receiver.ReceiveAsync<SampleValue>(reference, CancellationToken.None));

        Assert.Contains("trailing JSON", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisposingStoreInvalidatesOutstandingReferencesAndDeletesTempHandle()
    {
        var store = new SettingsTransferStore();
        var reference = store.Add(SettingsTransferSerializer.FromBytes(
            RandomNumberGenerator.GetBytes(1024)));

        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.Read(reference.TransferId, 0));
    }

    [Fact]
    public async Task CancellationRequestsProducerCleanupAndKeepsOriginalCancellation()
    {
        var reference = Reference([1]);
        var discarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = new SettingsTransferReceiver(
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            },
            (transferId, _) =>
            {
                discarded.TrySetResult(transferId);
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource();

        var receive = receiver.ReceiveAsync<byte[]>(reference, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        Assert.Equal(reference.TransferId, await discarded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ReceiverRejectsExpiredReferenceBeforeReadingChunks()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var reads = 0;
        var reference = Reference([1]) with
        {
            ExpiresAtUnixMilliseconds = time.GetUtcNow().AddMilliseconds(-1).ToUnixTimeMilliseconds(),
        };
        using var receiver = new SettingsTransferReceiver(
            (_, _, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(new SettingsTransferChunk(reference.TransferId, 0, [1], true));
            },
            timeProvider: time);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            receiver.ReceiveAsync<byte[]>(reference, CancellationToken.None));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task ReceiverDiscardsReferenceRejectedByConcurrentAdmission()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new ConcurrentQueue<string>();
        var started = 0;
        using var receiver = new SettingsTransferReceiver(
            async (id, offset, _) =>
            {
                if (Interlocked.Increment(ref started) == SettingsTransferProtocol.MaximumConcurrentTransfers)
                {
                    fourStarted.TrySetResult();
                }
                await release.Task;
                return new SettingsTransferChunk(id, offset, [(byte)'1'], Complete: true);
            },
            (id, _) =>
            {
                discarded.Enqueue(id);
                return Task.CompletedTask;
            });
        var activeReferences = Enumerable.Range(0, SettingsTransferProtocol.MaximumConcurrentTransfers)
            .Select(_ => Reference([(byte)'1']))
            .ToArray();
        var active = activeReferences
            .Select(reference => receiver.ReceiveAsync<int>(reference, CancellationToken.None))
            .ToArray();
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rejected = Reference([(byte)'1']);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receiver.ReceiveAsync<int>(rejected, CancellationToken.None));
        Assert.Contains(rejected.TransferId, discarded);

        release.TrySetResult();
        Assert.All(await Task.WhenAll(active), value => Assert.Equal(1, value));
    }

    [Fact]
    public async Task DuplicateRejectionDoesNotDiscardOriginalMultiChunkReceive()
    {
        var expected = new string('x', SettingsTransferProtocol.ChunkBytes + 100);
        var payload = SerializeBytes(expected);
        var reference = Reference(payload);
        var secondReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new ConcurrentQueue<string>();
        using var receiver = new SettingsTransferReceiver(
            async (id, offset, _) =>
            {
                if (offset > 0)
                {
                    secondReadStarted.TrySetResult();
                    await releaseSecondRead.Task;
                }
                var count = (int)Math.Min(SettingsTransferProtocol.ChunkBytes, payload.Length - offset);
                return new SettingsTransferChunk(
                    id,
                    offset,
                    payload.AsSpan(checked((int)offset), count).ToArray(),
                    offset + count == payload.Length);
            },
            (id, _) =>
            {
                discarded.Enqueue(id);
                return Task.CompletedTask;
            });

        var original = receiver.ReceiveAsync<string>(reference, CancellationToken.None);
        await secondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receiver.ReceiveAsync<string>(reference, CancellationToken.None));
        Assert.DoesNotContain(reference.TransferId, discarded);

        releaseSecondRead.TrySetResult();
        Assert.Equal(expected, await original);
        Assert.DoesNotContain(reference.TransferId, discarded);
    }

    private static SettingsTransferReceiver ReceiverFor(
        SettingsTransferReference reference,
        byte[] payload) =>
        new((id, offset, _) =>
        {
            Assert.Equal(reference.TransferId, id);
            var count = (int)Math.Min(SettingsTransferProtocol.ChunkBytes, payload.Length - offset);
            return Task.FromResult(new SettingsTransferChunk(
                id,
                offset,
                payload.AsSpan(checked((int)offset), count).ToArray(),
                offset + count == reference.Length));
        });

    private static SettingsTransferReference Reference(byte[] payload) =>
        new(
            SettingsTransferProtocol.Discriminator,
            Guid.NewGuid().ToString("N"),
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            (DateTimeOffset.UtcNow + SettingsTransferProtocol.TransferLifetime)
            .ToUnixTimeMilliseconds());

    private static byte[] SerializeBytes(object value)
    {
        using var payload = SettingsTransferSerializer.Serialize(value);
        var bytes = new byte[checked((int)payload.Length)];
        payload.Stream.ReadExactly(bytes);
        return bytes;
    }

    private sealed record SampleValue(string Name, int Revision);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
