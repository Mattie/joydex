using System.Text.Json;
using System.Text.Json.Nodes;

namespace Joydex.Windows.TaskAlerts;

public enum JoydexHookState
{
    NotInstalled,
    Installed,
    RepairNeeded,
}

public sealed class CodexHookManager(string hooksPath)
{
    public const string StatusMarker = "Joydex task status";
    private static readonly string[] SupportedEvents = ["UserPromptSubmit", "PermissionRequest", "Stop"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JoydexHookState Inspect(string relayPath)
    {
        var root = LoadRoot();
        var expectedCommand = BuildCommand(relayPath);
        var matches = SupportedEvents.Count(eventName => EventHasExactHandler(root, eventName, expectedCommand));
        var joydexHandlers = EnumerateHandlers(root).Count(IsJoydexHandler);
        if (matches == SupportedEvents.Length && joydexHandlers == SupportedEvents.Length)
        {
            return JoydexHookState.Installed;
        }

        return HasAnyJoydexHandler(root) ? JoydexHookState.RepairNeeded : JoydexHookState.NotInstalled;
    }

    public void InstallOrRepair(string relayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relayPath);
        if (!File.Exists(relayPath))
        {
            throw new FileNotFoundException("The Joydex hook relay is not present beside the application.", relayPath);
        }

        var root = LoadRoot();
        RemoveJoydexHandlers(root);
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var command = BuildCommand(relayPath);
        foreach (var eventName in SupportedEvents)
        {
            var eventGroups = hooks[eventName] as JsonArray;
            if (eventGroups is null)
            {
                eventGroups = [];
                hooks[eventName] = eventGroups;
            }

            eventGroups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = command,
                        ["commandWindows"] = command,
                        ["timeout"] = 1,
                        ["statusMessage"] = StatusMarker,
                    },
                },
            });
        }

        SaveRoot(root);
    }

    public void Remove()
    {
        var root = LoadRoot();
        if (RemoveJoydexHandlers(root))
        {
            SaveRoot(root);
        }
    }

    private JsonObject LoadRoot()
    {
        if (!File.Exists(hooksPath))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(File.ReadAllText(hooksPath)) as JsonObject
            ?? throw new InvalidDataException("Codex hooks.json must contain a JSON object.");
    }

    private void SaveRoot(JsonObject root)
    {
        var fullPath = Path.GetFullPath(hooksPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Codex hooks.json has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".joydex.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                root.WriteTo(writer, JsonOptions);
                writer.Flush();
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

    private static bool RemoveJoydexHandlers(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        var removed = false;
        foreach (var eventProperty in hooks.ToArray())
        {
            if (eventProperty.Value is not JsonArray groups)
            {
                continue;
            }

            foreach (var groupNode in groups.ToArray())
            {
                if (groupNode is not JsonObject group || group["hooks"] is not JsonArray handlers)
                {
                    continue;
                }

                foreach (var handler in handlers.ToArray())
                {
                    if (IsJoydexHandler(handler))
                    {
                        handlers.Remove(handler);
                        removed = true;
                    }
                }

                if (handlers.Count == 0)
                {
                    groups.Remove(group);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(eventProperty.Key);
            }
        }

        return removed;
    }

    private static bool HasAnyJoydexHandler(JsonObject root) =>
        EnumerateHandlers(root).Any(IsJoydexHandler);

    private static bool EventHasExactHandler(JsonObject root, string eventName, string command) =>
        EnumerateHandlers(root, eventName).Any(handler =>
            IsJoydexHandler(handler)
            && string.Equals(GetString(handler?["type"]), "command", StringComparison.Ordinal)
            && string.Equals(GetString(handler?["command"]), command, StringComparison.Ordinal)
            && string.Equals(GetString(handler?["commandWindows"]), command, StringComparison.Ordinal)
            && GetInt32(handler?["timeout"]) == 1);

    private static bool IsJoydexHandler(JsonNode? handler) =>
        handler is JsonObject
        && string.Equals(GetString(handler["statusMessage"]), StatusMarker, StringComparison.Ordinal);

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? GetInt32(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static IEnumerable<JsonNode?> EnumerateHandlers(JsonObject root, string? eventName = null)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            yield break;
        }

        var events = eventName is null ? hooks.ToArray() : hooks.Where(pair => pair.Key == eventName).ToArray();
        foreach (var eventProperty in events)
        {
            if (eventProperty.Value is not JsonArray groups)
            {
                continue;
            }

            foreach (var group in groups.OfType<JsonObject>())
            {
                if (group["hooks"] is not JsonArray handlers)
                {
                    continue;
                }

                foreach (var handler in handlers)
                {
                    yield return handler;
                }
            }
        }
    }

    private static string BuildCommand(string relayPath)
    {
        var fullPath = Path.GetFullPath(relayPath);
        return fullPath.Any(char.IsWhiteSpace) ? $"\"{fullPath}\"" : fullPath;
    }
}
