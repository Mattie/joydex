using System.Text.Json.Nodes;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class CodexHookManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "joydex-hook-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void InstallAndRemovePreserveExistingHandlers()
    {
        Directory.CreateDirectory(_directory);
        var hooksPath = Path.Combine(_directory, "hooks.json");
        var relayPath = Path.Combine(_directory, "Joydex.HookRelay.exe");
        File.WriteAllBytes(relayPath, [0]);
        File.WriteAllText(hooksPath, ExistingHooks);
        var manager = new CodexHookManager(hooksPath);

        manager.InstallOrRepair(relayPath);

        Assert.Equal(JoydexHookState.Installed, manager.Inspect(relayPath));
        var installed = JsonNode.Parse(File.ReadAllText(hooksPath))!;
        Assert.Contains("errorhelp.py hook-stop", installed.ToJsonString());
        Assert.Contains("request_timer.py hook-stop", installed.ToJsonString());
        Assert.Equal(3, CountMarker(installed));
        foreach (var handler in JoydexHandlers(installed))
        {
            var command = handler["command"]!.GetValue<string>();
            Assert.Equal(Path.GetFullPath(relayPath), command);
            Assert.Equal(command, handler["commandWindows"]!.GetValue<string>());
        }

        manager.Remove();

        var removed = JsonNode.Parse(File.ReadAllText(hooksPath))!;
        Assert.Contains("errorhelp.py hook-stop", removed.ToJsonString());
        Assert.Contains("request_timer.py hook-stop", removed.ToJsonString());
        Assert.Equal(0, CountMarker(removed));
    }

    [Fact]
    public void RepairReplacesStaleJoydexHandlerWithoutDuplicating()
    {
        Directory.CreateDirectory(_directory);
        var hooksPath = Path.Combine(_directory, "hooks.json");
        var relayPath = Path.Combine(_directory, "Joydex.HookRelay.exe");
        File.WriteAllBytes(relayPath, [0]);
        File.WriteAllText(hooksPath, $$"""
            { "hooks": { "Stop": [ { "hooks": [ {
              "type": "command", "command": "old.exe", "statusMessage": "{{CodexHookManager.StatusMarker}}"
            } ] } ] } }
            """);
        var manager = new CodexHookManager(hooksPath);

        Assert.Equal(JoydexHookState.RepairNeeded, manager.Inspect(relayPath));
        manager.InstallOrRepair(relayPath);

        var repaired = JsonNode.Parse(File.ReadAllText(hooksPath))!;
        Assert.Equal(3, CountMarker(repaired));
        Assert.DoesNotContain("old.exe", repaired.ToJsonString());
    }

    [Theory]
    [InlineData("process", 1)]
    [InlineData("command", 5)]
    public void InspectRequiresCommandTypeAndOneSecondTimeout(string type, int timeout)
    {
        Directory.CreateDirectory(_directory);
        var hooksPath = Path.Combine(_directory, "hooks.json");
        var relayPath = Path.Combine(_directory, "Joydex.HookRelay.exe");
        File.WriteAllBytes(relayPath, [0]);
        var command = Path.GetFullPath(relayPath).Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(hooksPath, $$"""
            {
              "hooks": {
                "UserPromptSubmit": [ { "hooks": [ {
                  "type": "{{type}}", "command": "{{command}}", "commandWindows": "{{command}}",
                  "timeout": {{timeout}}, "statusMessage": "{{CodexHookManager.StatusMarker}}"
                } ] } ],
                "PermissionRequest": [ { "hooks": [ {
                  "type": "{{type}}", "command": "{{command}}", "commandWindows": "{{command}}",
                  "timeout": {{timeout}}, "statusMessage": "{{CodexHookManager.StatusMarker}}"
                } ] } ],
                "Stop": [ { "hooks": [ {
                  "type": "{{type}}", "command": "{{command}}", "commandWindows": "{{command}}",
                  "timeout": {{timeout}}, "statusMessage": "{{CodexHookManager.StatusMarker}}"
                } ] } ]
              }
            }
            """);
        var manager = new CodexHookManager(hooksPath);

        Assert.Equal(JoydexHookState.RepairNeeded, manager.Inspect(relayPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static int CountMarker(JsonNode root) => root.ToJsonString()
        .Split(CodexHookManager.StatusMarker, StringSplitOptions.None)
        .Length - 1;

    private static IEnumerable<JsonObject> JoydexHandlers(JsonNode root) =>
        root["hooks"]!.AsObject()
            .SelectMany(eventProperty => eventProperty.Value!.AsArray())
            .SelectMany(group => group!["hooks"]!.AsArray())
            .OfType<JsonObject>()
            .Where(handler => handler["statusMessage"]?.GetValue<string>() == CodexHookManager.StatusMarker);

    private const string ExistingHooks = """
        {
          "hooks": {
            "Stop": [
              { "hooks": [ { "type": "command", "command": "python errorhelp.py hook-stop", "statusMessage": "ErrorHelp" } ] },
              { "hooks": [ { "type": "command", "command": "python request_timer.py hook-stop", "statusMessage": "Timer" } ] }
            ],
            "UserPromptSubmit": [
              { "hooks": [ { "type": "command", "command": "python request_timer.py submit", "statusMessage": "Timer" } ] }
            ]
          }
        }
        """;
}
