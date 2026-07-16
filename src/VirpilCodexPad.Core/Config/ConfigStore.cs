using System.Text.Json;

namespace VirpilCodexPad.Core.Config;

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

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<CompanionConfig>(json, SerializerOptions)
            ?? throw new InvalidDataException($"Configuration file '{path}' was empty.");

        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                $"Configuration file '{path}' is invalid:{Environment.NewLine}- "
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

        File.WriteAllText(path, JsonSerializer.Serialize(config, SerializerOptions));
    }
}
