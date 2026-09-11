using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Core.Voice;

public static class PebbleIndexPreferencesStore
{
    private const int MaximumDocumentBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) },
    };

    public static PebbleIndexPreferences LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            Save(path, PebbleIndexPreferences.Default);
            return PebbleIndexPreferences.Default;
        }
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumDocumentBytes)
        {
            throw new InvalidDataException($"The Pebble Index settings file must contain 1 to {MaximumDocumentBytes} bytes.");
        }
        try
        {
            var preferences = JsonSerializer.Deserialize<PebbleIndexPreferences>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new JsonException("The Pebble Index settings file was empty.");
            var normalized = preferences.Normalize();
            ThrowIfInvalid(normalized);
            return normalized;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Pebble Index settings file is not valid JSON.", exception);
        }
    }

    public static void Save(string path, PebbleIndexPreferences preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = preferences.Normalize();
        ThrowIfInvalid(normalized);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The Pebble Index settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                stream.WriteByte((byte)'\n');
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

    private static void ThrowIfInvalid(PebbleIndexPreferences preferences)
    {
        var errors = preferences.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException("The Pebble Index settings are invalid:" + Environment.NewLine + "- "
                + string.Join(Environment.NewLine + "- ", errors));
        }
    }
}
