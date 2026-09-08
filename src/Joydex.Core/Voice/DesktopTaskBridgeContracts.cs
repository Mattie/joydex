using System.Text.Json;
using System.Buffers.Binary;

namespace Joydex.Core.Voice;

public static class DesktopTaskBridgeProtocol
{
    public const int Version = 1;
    public const string PipeName = "Joydex.DesktopTasks.v1";
    public const int MaximumFrameBytes = 256 * 1024;

    public const string StatusMethod = "bridge.status";
    public const string ListTasksMethod = "threads.list";
    public const string ReadTaskMethod = "threads.read";
    public const string SendMessageMethod = "threads.send";
}

public sealed record DesktopTaskBridgeRequest(
    int Version,
    string Id,
    string Method,
    string SourceThreadId,
    JsonElement Arguments);

public sealed record DesktopTaskBridgeResponse(
    int Version,
    string Id,
    bool Success,
    JsonElement Result,
    string? Error = null);

public sealed record DesktopTaskSummary(
    string Id,
    string HostId,
    string Title,
    string Status,
    string? ProjectId,
    string? WorkingDirectory,
    long UpdatedAt,
    bool Pinned = false);

public sealed record DesktopTaskCatalog(IReadOnlyList<DesktopTaskSummary> Tasks);

public sealed record DesktopTaskDeliveryResult(
    string TaskId,
    string HostId,
    string Title,
    bool Queued,
    string Detail);

public static class DesktopTaskBridgeFraming
{
    public static async Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > maximumBytes)
        {
            throw new InvalidDataException($"Desktop task bridge frame exceeded {maximumBytes} bytes.");
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > maximumBytes)
        {
            throw new InvalidDataException($"Desktop task bridge frame exceeded {maximumBytes} bytes.");
        }

        var payload = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}
