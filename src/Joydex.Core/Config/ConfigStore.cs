using System.Text.Json;
using System.Text;

namespace Joydex.Core.Config;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    public static CompanionConfig LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            Save(path, CompanionConfig.CreateSafeDefault());
        }

        return ParseExisting(File.ReadAllBytes(path), path);
    }

    internal static CompanionConfig ParseExisting(byte[] documentBytes, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        using var stream = new MemoryStream(documentBytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var deserialized = JsonSerializer.Deserialize<CompanionConfig>(reader.ReadToEnd(), SerializerOptions)
            ?? throw new InvalidDataException($"Configuration file '{sourcePath}' was empty.");
        var config = CompanionConfigNormalizer.Normalize(deserialized);

        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                $"Configuration file '{sourcePath}' is invalid:{Environment.NewLine}- "
                + string.Join($"{Environment.NewLine}- ", errors));
        }

        return config;
    }

    public static void Save(string path, CompanionConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(config);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = CompanionConfigNormalizer.Normalize(config);
        File.WriteAllText(path, JsonSerializer.Serialize(normalized, SerializerOptions));
    }
}
