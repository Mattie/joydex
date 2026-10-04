using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Joydex.Ipc;

internal static class SettingsTransferMethods
{
    public const string ReadServer = "ipc/settings-transfer/server/read";
    public const string ReadClient = "ipc/settings-transfer/client/read";
    public const string DiscardServer = "ipc/settings-transfer/server/discard";
    public const string DiscardClient = "ipc/settings-transfer/client/discard";
    public const string PrepareTransferred = "ipc/settings-transfer/prepare";
}

internal static class SettingsTransferProtocol
{
    public const string Discriminator = "settings-transfer.v1";
    public const int ChunkBytes = 48 * 1024;
    public const int MaximumConcurrentTransfers = 4;
    public static readonly TimeSpan TransferLifetime = TimeSpan.FromMinutes(2);

    // JSON-RPC adds only a small object around the serialized value. Keeping this reserve means
    // every value selected for the inline path is guaranteed to remain below the frame limit.
    private const int EnvelopeReserveBytes = 512;

    public static bool FitsInline(long serializedLength) =>
        serializedLength <= Contracts.RuntimeProtocol.MaximumMessageBytes - EnvelopeReserveBytes;
}

internal sealed record SettingsTransferReference(
    string TransferKind,
    string TransferId,
    long Length,
    string Sha256,
    long ExpiresAtUnixMilliseconds);

internal sealed record SettingsTransferChunk(
    string TransferId,
    long Offset,
    byte[] Data,
    bool Complete);

internal sealed class SettingsTransferPayload(FileStream stream, long length, string sha256) : IDisposable
{
    public FileStream Stream { get; } = stream;

    public long Length { get; } = length;

    public string Sha256 { get; } = sha256;

    public void Dispose() => Stream.Dispose();
}

internal static class SettingsTransferFile
{
    public static FileStream Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-ipc-transfers");
        Directory.CreateDirectory(directory);
        return new FileStream(
            Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }
}

internal static class SettingsTransferSerializer
{
    private static readonly JsonSerializer Serializer = CreateSerializer();

    public static SettingsTransferPayload Serialize(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var stream = SettingsTransferFile.Create();
        try
        {
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       bufferSize: 4096,
                       leaveOpen: true))
            using (var jsonWriter = new JsonTextWriter(writer) { Formatting = Formatting.None })
            {
                lock (Serializer)
                {
                    Serializer.Serialize(jsonWriter, value);
                }
            }
            stream.Flush();
            var length = stream.Length;
            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            stream.Position = 0;
            return new SettingsTransferPayload(stream, length, hash);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static SettingsTransferPayload FromBytes(ReadOnlySpan<byte> payload)
    {
        var stream = SettingsTransferFile.Create();
        try
        {
            stream.Write(payload);
            stream.Flush();
            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            stream.Position = 0;
            return new SettingsTransferPayload(stream, payload.Length, hash);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static T Deserialize<T>(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        using var jsonReader = new JsonTextReader(reader) { MaxDepth = 64 };
        try
        {
            lock (Serializer)
            {
                var value = Serializer.Deserialize<T>(jsonReader)
                    ?? throw new InvalidDataException("The settings transfer contained no value.");
                if (jsonReader.Read())
                {
                    throw new InvalidDataException("The settings transfer contained trailing JSON data.");
                }
                return value;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The settings transfer contained malformed or trailing JSON data.",
                exception);
        }
    }

    public static T DeserializeInline<T>(JToken token) =>
        token.ToObject<T>(CreateSerializer())
        ?? throw new InvalidDataException("The runtime IPC response contained no value.");

    public static bool TryGetReference(JToken token, out SettingsTransferReference reference)
    {
        reference = null!;
        if (token is not JObject value)
        {
            return false;
        }
        var discriminator = value.Properties().FirstOrDefault(
            property => string.Equals(
                property.Name,
                nameof(SettingsTransferReference.TransferKind),
                StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(
                discriminator?.Value.Value<string>(),
                SettingsTransferProtocol.Discriminator,
                StringComparison.Ordinal))
        {
            return false;
        }
        reference = value.ToObject<SettingsTransferReference>(CreateSerializer())
            ?? throw new InvalidDataException("The settings transfer reference was empty.");
        return true;
    }

    private static JsonSerializer CreateSerializer() => JsonSerializer.Create(new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.None,
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
        MaxDepth = 64,
    });
}

internal sealed class SettingsTransferStore : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private int _count;
    private int _disposed;

    public SettingsTransferStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SettingsTransferReference Add(SettingsTransferPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length <= 0)
        {
            payload.Dispose();
            throw new InvalidDataException("The settings transfer must contain at least one byte.");
        }
        lock (_gate)
        {
            if (_disposed != 0)
            {
                payload.Dispose();
                throw new ObjectDisposedException(nameof(SettingsTransferStore));
            }
            var now = _timeProvider.GetUtcNow();
            foreach (var expired in _entries
                         .Where(pair => pair.Value.ExpiresAtUtc <= now)
                         .ToArray())
            {
                if (_entries.TryRemove(expired.Key, out var removed))
                {
                    _count--;
                    lock (removed)
                    {
                        removed.Dispose();
                    }
                }
            }
            if (_count >= SettingsTransferProtocol.MaximumConcurrentTransfers)
            {
                payload.Dispose();
                throw new InvalidOperationException("The runtime IPC connection is at its settings transfer limit.");
            }

            var id = Guid.NewGuid().ToString("N");
            var expiresAt = _timeProvider.GetUtcNow() + SettingsTransferProtocol.TransferLifetime;
            var entry = new Entry(payload, expiresAt);
            if (!_entries.TryAdd(id, entry))
            {
                payload.Dispose();
                throw new InvalidOperationException("A unique settings transfer ID could not be allocated.");
            }
            _count++;
            return new SettingsTransferReference(
                SettingsTransferProtocol.Discriminator,
                id,
                payload.Length,
                payload.Sha256,
                expiresAt.ToUnixTimeMilliseconds());
        }
    }

    public SettingsTransferChunk Read(string transferId, long offset)
    {
        ValidateId(transferId);
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_entries.TryGetValue(transferId, out entry!))
            {
                throw new InvalidOperationException("The settings transfer is unknown, complete, or already consumed.");
            }
            Monitor.Enter(entry);
        }

        SettingsTransferChunk? chunk = null;
        var expired = false;
        try
        {
            if (entry.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                expired = true;
            }
            else
            {
                if (offset != entry.Offset)
                {
                    throw new InvalidOperationException(
                        $"The settings transfer expected offset {entry.Offset}, but received {offset}.");
                }
                var count = (int)Math.Min(SettingsTransferProtocol.ChunkBytes, entry.Length - offset);
                if (count <= 0)
                {
                    throw new InvalidOperationException("The settings transfer has already completed.");
                }
                var data = new byte[count];
                entry.Payload.Stream.Position = offset;
                entry.Payload.Stream.ReadExactly(data);
                entry.Offset += count;
                var complete = entry.Offset == entry.Length;
                chunk = new SettingsTransferChunk(transferId, offset, data, complete);
            }
        }
        finally
        {
            Monitor.Exit(entry);
        }

        if (expired || chunk!.Complete)
        {
            Discard(transferId);
        }
        if (expired)
        {
            throw new InvalidOperationException("The settings transfer expired before it was consumed.");
        }
        return chunk!;
    }

    public void Discard(string transferId)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryRemove(transferId, out entry))
            {
                return;
            }
            _count--;
        }
        lock (entry)
        {
            entry.Dispose();
        }
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }
            _disposed = 1;
            entries = _entries.Values.ToArray();
            _entries.Clear();
            _count = 0;
        }
        foreach (var entry in entries)
        {
            lock (entry)
            {
                entry.Dispose();
            }
        }
    }

    private static void ValidateId(string transferId)
    {
        if (string.IsNullOrEmpty(transferId)
            || transferId.Length != 32
            || !Guid.TryParseExact(transferId, "N", out _))
        {
            throw new InvalidDataException("The settings transfer ID is invalid.");
        }
    }

    private sealed class Entry(SettingsTransferPayload payload, DateTimeOffset expiresAtUtc) : IDisposable
    {
        public SettingsTransferPayload Payload { get; } = payload;

        public long Length => Payload.Length;

        public long Offset { get; set; }

        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        public void Dispose() => Payload.Dispose();
    }
}

internal sealed class SettingsTransferReceiver : IDisposable
{
    private const int MaximumReferencesPerConnection = 4096;
    private readonly object _seenGate = new();
    private readonly Func<string, long, CancellationToken, Task<SettingsTransferChunk>> _readChunk;
    private readonly Func<string, CancellationToken, Task> _discard;
    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private int _active;
    private int _disposed;

    public SettingsTransferReceiver(JsonRpc rpc, string readMethod, string discardMethod)
        : this(
            (transferId, offset, cancellationToken) =>
            rpc.InvokeWithCancellationAsync<SettingsTransferChunk>(
                readMethod,
                [transferId, offset],
                cancellationToken),
            (transferId, cancellationToken) =>
                rpc.InvokeWithCancellationAsync(
                    discardMethod,
                    [transferId],
                    cancellationToken),
            TimeProvider.System)
    {
    }

    internal SettingsTransferReceiver(
        Func<string, long, CancellationToken, Task<SettingsTransferChunk>> readChunk,
        Func<string, CancellationToken, Task>? discard = null,
        TimeProvider? timeProvider = null)
    {
        _readChunk = readChunk;
        _discard = discard ?? ((_, _) => Task.CompletedTask);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<T> ReceiveAsync<T>(
        SettingsTransferReference reference,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ValidateReference(reference, _timeProvider.GetUtcNow());
        var activeReserved = false;
        var discardOnFailure = true;
        try
        {
            if (Interlocked.Increment(ref _active) > SettingsTransferProtocol.MaximumConcurrentTransfers)
            {
                Interlocked.Decrement(ref _active);
                throw new InvalidOperationException("The runtime IPC connection is at its inbound settings transfer limit.");
            }
            activeReserved = true;
            lock (_seenGate)
            {
                var nowMilliseconds = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                foreach (var expiredId in _seen
                             .Where(pair => pair.Value <= nowMilliseconds)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _seen.Remove(expiredId);
                }
                if (_seen.Count >= MaximumReferencesPerConnection)
                {
                    throw new InvalidOperationException("The runtime IPC connection exhausted its active settings transfer reference limit.");
                }
                if (!_seen.TryAdd(reference.TransferId, reference.ExpiresAtUnixMilliseconds))
                {
                    discardOnFailure = false;
                    throw new InvalidOperationException("The settings transfer reference was already used on this connection.");
                }
            }
            using var transferDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            transferDeadline.CancelAfter(SettingsTransferProtocol.TransferLifetime);
            using var destination = SettingsTransferFile.Create();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < reference.Length)
            {
                transferDeadline.Token.ThrowIfCancellationRequested();
                var chunk = await _readChunk(reference.TransferId, offset, transferDeadline.Token)
                    .ConfigureAwait(false);
                ValidateChunk(reference, chunk, offset);
                await destination.WriteAsync(chunk.Data, transferDeadline.Token).ConfigureAwait(false);
                hash.AppendData(chunk.Data);
                offset += chunk.Data.Length;
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualHash, reference.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The settings transfer SHA-256 did not match its declaration.");
            }
            if (destination.Length != reference.Length)
            {
                throw new InvalidDataException("The settings transfer did not match its declared length.");
            }
            return SettingsTransferSerializer.Deserialize<T>(destination);
        }
        catch
        {
            if (discardOnFailure)
            {
                using var discardDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _discard(reference.TransferId, discardDeadline.Token).ConfigureAwait(false);
                }
                catch
                {
                    // The original transfer failure remains the useful error. Disconnect cleanup is the fallback.
                }
            }
            throw;
        }
        finally
        {
            if (activeReserved)
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        lock (_seenGate)
        {
            _seen.Clear();
        }
    }

    private static void ValidateReference(SettingsTransferReference reference, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        var latestAllowedExpiration = (now + SettingsTransferProtocol.TransferLifetime)
            .ToUnixTimeMilliseconds();
        if (!string.Equals(
                reference.TransferKind,
                SettingsTransferProtocol.Discriminator,
                StringComparison.Ordinal)
            || string.IsNullOrEmpty(reference.TransferId)
            || reference.TransferId.Length != 32
            || !Guid.TryParseExact(reference.TransferId, "N", out _)
            || reference.Length <= 0
            || string.IsNullOrEmpty(reference.Sha256)
            || reference.Sha256.Length != 64
            || reference.Sha256.Any(character => !char.IsAsciiHexDigit(character))
            || reference.ExpiresAtUnixMilliseconds <= nowMilliseconds
            || reference.ExpiresAtUnixMilliseconds > latestAllowedExpiration)
        {
            throw new InvalidDataException("The settings transfer reference is invalid.");
        }
    }

    private static void ValidateChunk(
        SettingsTransferReference reference,
        SettingsTransferChunk chunk,
        long expectedOffset)
    {
        if (chunk is null
            || !string.Equals(chunk.TransferId, reference.TransferId, StringComparison.Ordinal)
            || chunk.Offset != expectedOffset
            || chunk.Data is null
            || chunk.Data.Length == 0
            || chunk.Data.Length > SettingsTransferProtocol.ChunkBytes
            || expectedOffset > reference.Length - chunk.Data.Length
            || chunk.Data.Length != (int)Math.Min(SettingsTransferProtocol.ChunkBytes, reference.Length - expectedOffset)
            || chunk.Complete != (expectedOffset + chunk.Data.Length == reference.Length))
        {
            throw new InvalidDataException("The settings transfer chunk violated its declared order or length.");
        }
    }

}
