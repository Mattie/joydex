using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Joydex.Secrets;

internal static class SecretsAtomicFile
{
    public static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The secrets data path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, options);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}

internal static class SecretsFileLock
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static TResult WithLock<TResult>(string path, Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var lease = Acquire(path);
        return action();
    }

    public static IDisposable Acquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var mutex = new Mutex(false, "Local\\Joydex.Secrets.File." + digest);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(Timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
            {
                throw new TimeoutException("The Secrets data file is busy.");
            }
            return new MutexLease(mutex);
        }
        catch
        {
            if (acquired) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _mutex, null);
            if (current is null) return;
            current.ReleaseMutex();
            current.Dispose();
        }
    }
}
