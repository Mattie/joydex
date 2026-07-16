using System.Text.Json;

namespace VirpilCodexPad.App;

internal sealed record ConfigurationWindowState(int Width, int Height, bool Maximized);

internal static class ConfigurationWindowStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ConfigurationWindowState? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<ConfigurationWindowState>(
                File.ReadAllText(path),
                SerializerOptions);

            return state is { Width: > 0, Height: > 0 } ? state : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, ConfigurationWindowState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(state, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Window state is optional and must never prevent the configuration dialog from closing.
        }
    }
}
