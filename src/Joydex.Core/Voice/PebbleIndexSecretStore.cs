using System.Security.Cryptography;

namespace Joydex.Core.Voice;

public static class PebbleIndexSecretStore
{
    public static string LoadOrCreate(string path) =>
        LoadOrCreate(path, static () => { });

    internal static string LoadOrCreate(string path, Action beforePublish)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(beforePublish);
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return ReadExisting(fullPath);
        }
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The secret path has no parent directory.");
        Directory.CreateDirectory(directory);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        beforePublish();
        if (!TryPublishNew(fullPath, secret))
        {
            return ReadExisting(fullPath);
        }
        return secret;
    }

    private static string ReadExisting(string path)
    {
        var existing = File.ReadAllText(path).Trim();
        if (existing.Length >= 8 && existing.Length <= 256 && !existing.Any(char.IsWhiteSpace)) return existing;
        throw new InvalidDataException("The Pebble Index authorization secret is invalid.");
    }

    private static bool TryPublishNew(string path, string secret)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNew(temporary, secret);
            try
            {
                File.Move(temporary, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteNew(string path, string secret)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.WriteLine(secret);
        writer.Flush();
        stream.Flush(true);
    }
}
