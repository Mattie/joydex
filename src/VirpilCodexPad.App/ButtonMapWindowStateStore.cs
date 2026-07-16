using System.Text.Json;

namespace VirpilCodexPad.App;

internal sealed record ButtonMapWindowState(
    int Left,
    int Top,
    int Width,
    int Height,
    bool Maximized);

internal static class ButtonMapWindowStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ButtonMapWindowState? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<ButtonMapWindowState>(
                File.ReadAllText(path),
                SerializerOptions);

            return state is { Width: >= 640, Height: >= 480 } ? state : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, ButtonMapWindowState state)
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
            // Quick-reference placement is optional and must never affect input handling.
        }
    }
}
