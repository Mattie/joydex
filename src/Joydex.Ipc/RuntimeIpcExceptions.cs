namespace Joydex.Ipc;

/// <summary>Stable application error codes returned by the runtime IPC endpoint.</summary>
public static class RuntimeIpcErrorCodes
{
    // Application-defined codes sit outside JSON-RPC's reserved -32768 through -32000 range.
    public const int RequestOverloaded = -33001;
    public const int MessageTooLarge = -33002;
}

public sealed class RuntimeIpcAuthenticationException(string message) : IOException(message);

public sealed class RuntimeIpcOwnerAlreadyRunningException(
    string message,
    string? existingDataRootId = null,
    Exception? innerException = null) : IOException(message, innerException)
{
    public string? ExistingDataRootId { get; } = existingDataRootId;
}

public sealed class RuntimeIpcMessageTooLargeException(int actualBytes, int maximumBytes)
    : IOException(
        $"The runtime IPC message declared {actualBytes} bytes; the limit is {maximumBytes} bytes.")
{
    public int ActualBytes { get; } = actualBytes;

    public int MaximumBytes { get; } = maximumBytes;
}
