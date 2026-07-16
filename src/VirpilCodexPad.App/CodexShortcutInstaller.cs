using System.Text.Json;
using System.Text.Json.Nodes;

namespace VirpilCodexPad.App;

internal static class CodexShortcutInstaller
{
    private static readonly (string Command, string Key, string LegacyKey)[] ManagedBindings =
    [
        ("thread1", "Ctrl+Alt+Shift+F1", "F13"),
        ("thread2", "Ctrl+Alt+Shift+F2", "F14"),
        ("thread3", "Ctrl+Alt+Shift+F3", "F15"),
        ("thread4", "Ctrl+Alt+Shift+F4", "F16"),
        ("thread5", "Ctrl+Alt+Shift+F5", "F17"),
        ("thread6", "Ctrl+Alt+Shift+F6", "F18"),
        ("composer.toggleFastMode", "Ctrl+Alt+Shift+F7", "F19"),
        ("approval.approve", "Ctrl+Alt+Shift+F8", "F20"),
        ("approval.decline", "Ctrl+Alt+Shift+F9", "F21"),
        ("forkThread", "Ctrl+Alt+Shift+F10", "F22"),
        ("composer.submit", "Ctrl+Alt+Shift+F11", "F23"),
        ("composer.togglePlanMode", "Ctrl+Alt+Shift+F12", "F24"),
        ("composer.increaseReasoningEffort", "Ctrl+Alt+PageUp", "Ctrl+Alt+PageUp"),
        ("composer.decreaseReasoningEffort", "Ctrl+Alt+PageDown", "Ctrl+Alt+PageDown"),
    ];

    public static string KeybindingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "keybindings.json");

    public static void Install()
    {
        var bindings = LoadBindings();

        var managedCommands = ManagedBindings
            .Select(binding => binding.Command)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = bindings.Count - 1; index >= 0; index--)
        {
            var command = bindings[index]?["command"]?.GetValue<string>();
            if (command is not null && managedCommands.Contains(command))
            {
                bindings.RemoveAt(index);
            }
        }

        foreach (var (command, key, _) in ManagedBindings)
        {
            bindings.Add(new JsonObject
            {
                ["command"] = command,
                ["key"] = key,
            });
        }

        SaveBindings(bindings);
    }

    public static bool MigrateLegacyExtendedFunctionBindings()
    {
        if (!File.Exists(KeybindingsPath))
        {
            return false;
        }

        var bindings = LoadBindings();
        var changed = false;
        foreach (var node in bindings)
        {
            var command = node?["command"]?.GetValue<string>();
            var key = node?["key"]?.GetValue<string>();
            var replacement = ManagedBindings.FirstOrDefault(binding =>
                string.Equals(binding.Command, command, StringComparison.OrdinalIgnoreCase)
                && string.Equals(binding.LegacyKey, key, StringComparison.OrdinalIgnoreCase));

            if (replacement.Command is null || string.Equals(replacement.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            node!["key"] = replacement.Key;
            changed = true;
        }

        if (changed)
        {
            SaveBindings(bindings);
        }

        return changed;
    }

    private static JsonArray LoadBindings() => File.Exists(KeybindingsPath)
        ? JsonNode.Parse(File.ReadAllText(KeybindingsPath)) as JsonArray
            ?? throw new InvalidDataException("Codex keybindings.json must contain a JSON array.")
        : [];

    private static void SaveBindings(JsonArray bindings)
    {
        var path = KeybindingsPath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Could not resolve the Codex configuration directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $"keybindings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, bindings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
