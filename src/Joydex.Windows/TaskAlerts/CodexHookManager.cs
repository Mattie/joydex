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
    public const int TimeoutSeconds = 5;
    private static readonly HookDefinition[] SupportedHooks =
    [
        new("UserPromptSubmit"),
        new("PermissionRequest"),
        new("PreToolUse", "^request_user_input$"),
        new("PostToolUse", "^(Bash|apply_patch|request_user_input|mcp__.*)$"),
        new("Stop"),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JoydexHookState Inspect(string relayPath)
    {
        var root = LoadRoot();
        var expectedCommand = BuildCommand(relayPath);
        var matches = SupportedHooks.Count(hook => EventHasExactHandler(root, hook, expectedCommand));
        var joydexHandlers = EnumerateHandlers(root).Count(IsJoydexHandler);
        if (matches == SupportedHooks.Length && joydexHandlers == SupportedHooks.Length)
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
        foreach (var hook in SupportedHooks)
        {
            var eventGroups = hooks[hook.EventName] as JsonArray;
            if (eventGroups is null)
            {
                eventGroups = [];
                hooks[hook.EventName] = eventGroups;
            }

            var handler = new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["commandWindows"] = command,
                ["timeout"] = TimeoutSeconds,
                ["statusMessage"] = StatusMarker,
            };

            var group = new JsonObject
            {
                ["hooks"] = new JsonArray { handler },
            };
            if (hook.Matcher is not null)
            {
                group["matcher"] = hook.Matcher;
            }

            eventGroups.Add(group);
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

    private static bool EventHasExactHandler(JsonObject root, HookDefinition hook, string command)
    {
        if (root["hooks"]?[hook.EventName] is not JsonArray groups)
        {
            return false;
        }

        return groups.OfType<JsonObject>().Any(group =>
            string.Equals(GetString(group["matcher"]), hook.Matcher, StringComparison.Ordinal)
            && group["hooks"] is JsonArray handlers
            && handlers.Any(handler =>
                IsJoydexHandler(handler)
                && string.Equals(GetString(handler?["type"]), "command", StringComparison.Ordinal)
                && string.Equals(GetString(handler?["command"]), command, StringComparison.Ordinal)
                && string.Equals(GetString(handler?["commandWindows"]), command, StringComparison.Ordinal)
                && GetInt32(handler?["timeout"]) == TimeoutSeconds
                && handler?["async"] is null));
    }

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

    private sealed record HookDefinition(string EventName, string? Matcher = null);
}
