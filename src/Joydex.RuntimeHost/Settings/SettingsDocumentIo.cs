using System.Security.Cryptography;

namespace Joydex.RuntimeHost.Settings;

internal interface ISettingsDocumentIo
{
    bool Exists(string path);
    byte[] Read(string path);
    T Read<T>(string path, Func<Stream, T> read);
    void Replace(string path, ReadOnlySpan<byte> content);
    void Replace(string path, Action<Stream> write);
    void Delete(string path);
}

internal sealed class SettingsDocumentIo : ISettingsDocumentIo
{
    public bool Exists(string path) => File.Exists(path);

    public byte[] Read(string path) => File.ReadAllBytes(path);

    public T Read<T>(string path, Func<Stream, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        return read(stream);
    }

    public void Replace(string path, ReadOnlySpan<byte> content)
    {
        var bytes = content.ToArray();
        Replace(path, stream => stream.Write(bytes));
    }

    public void Replace(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("A settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

internal readonly record struct SettingsFileFingerprint(bool Exists, string ContentHash)
{
    public static SettingsFileFingerprint Read(ISettingsDocumentIo io, string path)
    {
        byte[] content;
        try
        {
            content = io.Read(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SettingsFileFingerprint(false, "MISSING");
        }

        return new SettingsFileFingerprint(
            true,
            Convert.ToHexString(SHA256.HashData(content)));
    }
}
