using System.Text.Json;
using Joydex.Core.TaskAlerts;

namespace Joydex.Windows.TaskAlerts;

/// <summary>Private display labels, kept separate from lifecycle authority and its restart state.</summary>
internal sealed class TaskAlertMetadataStore(string path)
{
    private const int MaximumEntries = 2048;
    private readonly Dictionary<string, TaskAlertMetadata> _items = Load(path);

    public TaskAlertMetadata? Get(string key) => _items.GetValueOrDefault(key);

    public bool Set(string key, TaskAlertMetadata value)
    {
        var bounded = new TaskAlertMetadata(Limit(value.Title), Limit(value.Project), Limit(value.Provider), Limit(value.Model));
        if (_items.GetValueOrDefault(key) == bounded) return false;
        _items[key] = bounded;
        while (_items.Count > MaximumEntries) _items.Remove(_items.Keys.First());
        return true;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_items));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Dictionary<string, TaskAlertMetadata> Load(string source)
    {
        try
        {
            if (!File.Exists(source) || new FileInfo(source).Length > 4 * 1024 * 1024) return new(StringComparer.Ordinal);
            return (JsonSerializer.Deserialize<Dictionary<string, TaskAlertMetadata>>(File.ReadAllText(source)) ?? [])
                .Where(pair => pair.Value is not null).TakeLast(MaximumEntries).ToDictionary(pair => pair.Key, pair => new TaskAlertMetadata(
                    Limit(pair.Value.Title), Limit(pair.Value.Project), Limit(pair.Value.Provider), Limit(pair.Value.Model)), StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        { return new(StringComparer.Ordinal); }
    }

    private static string? Limit(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        new string(value.Where(character => !char.IsControl(character)).Take(256).ToArray());
}
