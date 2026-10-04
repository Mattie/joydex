using System.Diagnostics;
using System.Text.Json;

namespace Joydex.Ipc;

internal sealed class RuntimeIpcProductionLease : IDisposable
{
    private readonly FileStream _lock;

    private RuntimeIpcProductionLease(FileStream fileLock)
    {
        _lock = fileLock;
    }

    public static RuntimeIpcProductionLease Acquire(RuntimeIpcEndpoint endpoint)
    {
        if (endpoint.InstanceKind != Contracts.RuntimeInstanceKind.Production)
        {
            throw new ArgumentException("A production lease requires a production endpoint.", nameof(endpoint));
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Joydex",
            "Runtime");
        Directory.CreateDirectory(directory);
        var userKey = RuntimeIpcEndpoint.Hash(RuntimeIpcEndpoint.GetCurrentUserSid())[..16];
        var path = Path.Combine(directory, $"owner-{userKey}-{endpoint.SessionId}.lock");

        FileStream fileLock;
        try
        {
            fileLock = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            var existingRoot = TryReadDataRootId(path);
            var message = existingRoot is not null
                          && !string.Equals(existingRoot, endpoint.DataRootId, StringComparison.Ordinal)
                ? "Another production runtime in this user session already owns a different data root."
                : "Another production runtime already owns this user session.";
            throw new RuntimeIpcOwnerAlreadyRunningException(message, existingRoot, exception);
        }

        try
        {
            var metadata = new LeaseMetadata(
                SchemaVersion: 1,
                endpoint.DataRootId,
                endpoint.PipeName,
                Environment.ProcessId,
                DateTimeOffset.UtcNow);
            fileLock.SetLength(0);
            JsonSerializer.Serialize(fileLock, metadata);
            fileLock.Flush(flushToDisk: true);
            fileLock.Position = 0;
            return new RuntimeIpcProductionLease(fileLock);
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }
    }

    public void Dispose() => _lock.Dispose();

    private static string? TryReadDataRootId(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<LeaseMetadata>(stream)?.DataRootId;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private sealed record LeaseMetadata(
        int SchemaVersion,
        string DataRootId,
        string PipeName,
        int ProcessId,
        DateTimeOffset StartedAtUtc);
}
