using System.Text.Json;

namespace Joydex.Windows.Actions;

/// <summary>
/// Verified app-declared defaults for conservative task-seed checks, not a renderer routing model.
/// </summary>
internal static class CodexWindowsDefaultBindings
{
    public static IReadOnlyDictionary<string, string[]> All { get; } = Load();

    private static IReadOnlyDictionary<string, string[]> Load()
    {
        using var stream = typeof(CodexWindowsDefaultBindings).Assembly.GetManifestResourceStream(
            "Joydex.Windows.Actions.Compatibility.CodexWindows26930.json")
            ?? throw new InvalidOperationException("The verified Windows shortcut inventory is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("commands").EnumerateObject().ToDictionary(
            command => command.Name,
            command => command.Value.EnumerateArray().Select(key => key.GetString()!).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }
}
