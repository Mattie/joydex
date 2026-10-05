namespace Joydex.Core.Voice;

/// <summary>
/// Holds exclusive cross-process ownership of one normalized Pebble Index inbox directory.
/// The stable lease file remains in the directory while its open handle carries ownership.
/// </summary>
public sealed class PebbleIndexReceiverLease : IDisposable
{
    private const string LeaseFileName = ".joydex-pebble-index.receiver";
    private FileStream? _stream;

    private PebbleIndexReceiverLease(FileStream stream)
    {
        _stream = stream;
    }

    public static PebbleIndexReceiverLease Acquire(string inboxDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inboxDirectory);
        var directory = Path.GetFullPath(inboxDirectory);
        Directory.CreateDirectory(directory);
        try
        {
            var stream = new FileStream(
                Path.Combine(directory, LeaseFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new PebbleIndexReceiverLease(stream);
        }
        catch (IOException exception)
        {
            throw new IOException(
                "The Pebble Index inbox is already owned by another receiver.",
                exception);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
