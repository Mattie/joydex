using System.Security.Cryptography;

namespace Joydex.Core.Voice;

public static class PebbleIndexSecretStore
{
    public static string LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 8 && existing.Length <= 256 && !existing.Any(char.IsWhiteSpace)) return existing;
            throw new InvalidDataException("The Pebble Index authorization secret is invalid.");
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("The secret path has no parent directory.");
        Directory.CreateDirectory(directory);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.WriteLine(secret);
        writer.Flush();
        stream.Flush(true);
        return secret;
    }
}
