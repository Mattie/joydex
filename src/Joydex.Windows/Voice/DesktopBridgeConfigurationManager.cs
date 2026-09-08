using System.Text;
using System.Diagnostics;

namespace Joydex.Windows.Voice;

public enum DesktopBridgeConfigurationState
{
    NotInstalled,
    Installed,
    RepairNeeded,
    Conflict,
}

public sealed record DesktopBridgeConfigurationStatus(
    DesktopBridgeConfigurationState State,
    string Message);

/// <summary>
/// Owns only Joydex's marker-delimited Codex Desktop MCP block.
/// </summary>
public sealed class DesktopBridgeConfigurationManager(string configPath, string? packagedAdapterRoot = null)
{
    internal const string BeginMarker = "# BEGIN JOYDEX DESKTOP TASK BRIDGE (managed by Joydex)";
    internal const string EndMarker = "# END JOYDEX DESKTOP TASK BRIDGE (managed by Joydex)";
    private const string Section = "[mcp_servers.joydex_desktop_task_bridge]";
    private readonly string _configPath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(configPath)
            ? throw new ArgumentException("A Codex config path is required.", nameof(configPath))
            : configPath);
    private readonly string? _packagedAdapterRoot = packagedAdapterRoot;

    public static string DefaultConfigPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new InvalidOperationException("The current user profile directory is unavailable.");
        }
        return Path.Combine(profile, ".codex", "config.toml");
    }

    public DesktopBridgeConfigurationStatus Inspect(string hostExecutablePath)
    {
        var expected = BuildManagedBlock(hostExecutablePath, ResolvePackagedAdapterRoot());
        var text = File.Exists(_configPath) ? File.ReadAllText(_configPath, Encoding.UTF8) : string.Empty;
        var range = FindManagedRange(text);
        if (range.Conflict is { } conflict)
        {
            return new DesktopBridgeConfigurationStatus(DesktopBridgeConfigurationState.Conflict, conflict);
        }
        if (range.Start < 0)
        {
            return ContainsSection(text)
                ? new DesktopBridgeConfigurationStatus(
                    DesktopBridgeConfigurationState.Conflict,
                    "Codex config contains an unmanaged joydex_desktop_task_bridge section.")
                : new DesktopBridgeConfigurationStatus(
                    DesktopBridgeConfigurationState.NotInstalled,
                    "Desktop Task Bridge is not installed.");
        }

        var current = text[range.Start..range.End];
        return string.Equals(NormalizeNewlines(current).Trim(), expected.Trim(), StringComparison.Ordinal)
            ? new DesktopBridgeConfigurationStatus(
                DesktopBridgeConfigurationState.Installed,
                "Desktop Task Bridge compatibility metadata is installed. Joydex starts the bridge when Room Voice runs.")
            : new DesktopBridgeConfigurationStatus(
                DesktopBridgeConfigurationState.RepairNeeded,
                "The managed Desktop Task Bridge block does not match this Joydex build.");
    }

    public void InstallOrRepair(string hostExecutablePath)
    {
        var expected = BuildManagedBlock(hostExecutablePath, ResolvePackagedAdapterRoot());
        var text = File.Exists(_configPath) ? File.ReadAllText(_configPath, Encoding.UTF8) : string.Empty;
        var range = FindManagedRange(text);
        if (range.Conflict is { } conflict)
        {
            throw new InvalidDataException(conflict);
        }
        if (range.Start < 0 && ContainsSection(text))
        {
            throw new InvalidDataException(
                "Codex config contains an unmanaged mcp_servers.joydex_desktop_task_bridge section. Joydex did not alter it.");
        }

        var updated = range.Start >= 0
            ? text[..range.Start] + expected + text[range.End..]
            : AppendBlock(text, expected);
        WriteAtomically(updated);
    }

    public void Remove()
    {
        if (!File.Exists(_configPath))
        {
            return;
        }

        var text = File.ReadAllText(_configPath, Encoding.UTF8);
        var range = FindManagedRange(text);
        if (range.Conflict is { } conflict)
        {
            throw new InvalidDataException(conflict);
        }
        if (range.Start < 0)
        {
            if (ContainsSection(text))
            {
                throw new InvalidDataException(
                    "Codex config contains an unmanaged joydex_desktop_task_bridge section. Joydex did not alter it.");
            }
            return;
        }

        var updated = text.Remove(range.Start, range.End - range.Start).TrimEnd() + Environment.NewLine;
        WriteAtomically(updated);
    }

    internal static string BuildManagedBlock(string hostExecutablePath, string packagedAdapterRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutablePath);
        var fullPath = Path.GetFullPath(hostExecutablePath.Trim());
        var adapterRoot = Path.GetFullPath(packagedAdapterRoot.Trim());
        if (!File.Exists(Path.Combine(adapterRoot, "server.mjs")))
        {
            throw new FileNotFoundException("The packaged Codex App Tools adapter is missing.", adapterRoot);
        }
        if (fullPath.Contains('\'') || adapterRoot.Contains('\''))
        {
            throw new InvalidDataException("The Desktop Task Bridge path cannot contain an apostrophe.");
        }

        return string.Join('\n',
            BeginMarker,
            Section,
            $"command = '{fullPath}'",
            "args = []",
            "enabled = false",
            "disabled_tools = [\"joydex_desktop_bridge_status\"]",
            "env_vars = [\"CODEX_APP_TOOLS_PIPE_PATH\", \"CODEX_MCP_NODE_PATH\", \"CODEX_BROWSER_USE_NODE_PATH\", \"CODEX_ELECTRON_RESOURCES_PATH\", \"CODEX_CLI_PATH\", \"USERPROFILE\", \"LOCALAPPDATA\", \"PATH\"]",
            "startup_timeout_sec = 10",
            "tool_timeout_sec = 60",
            string.Empty,
            "[mcp_servers.joydex_desktop_task_bridge.env]",
            $"JOYDEX_CODEX_APP_TOOLS_ROOT = '{adapterRoot}'",
            EndMarker,
            string.Empty);
    }

    private string ResolvePackagedAdapterRoot()
    {
        if (!string.IsNullOrWhiteSpace(_packagedAdapterRoot))
        {
            return _packagedAdapterRoot;
        }

        var resources = Environment.GetEnvironmentVariable("CODEX_ELECTRON_RESOURCES_PATH")?.Trim();
        if (!string.IsNullOrWhiteSpace(resources))
        {
            var candidate = AdapterBelowResources(resources);
            if (File.Exists(Path.Combine(candidate, "server.mjs")))
            {
                return candidate;
            }
        }

        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is not { Length: > 0 } executable)
                    {
                        continue;
                    }
                    var appDirectory = Path.GetDirectoryName(executable);
                    if (appDirectory is null)
                    {
                        continue;
                    }
                    var candidate = AdapterBelowResources(Path.Combine(appDirectory, "resources"));
                    if (File.Exists(Path.Combine(candidate, "server.mjs")))
                    {
                        return candidate;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                }
            }
        }

        throw new InvalidOperationException(
            "The packaged Codex App Tools adapter could not be found. Start or update Codex Desktop, then repair the bridge.");
    }

    private static string AdapterBelowResources(string resources) => Path.Combine(
        resources,
        "plugins",
        "openai-bundled",
        "plugins",
        "codex-app-tools");

    private static string AppendBlock(string text, string block)
    {
        var prefix = text.TrimEnd();
        return prefix.Length == 0
            ? block
            : prefix + Environment.NewLine + Environment.NewLine + block;
    }

    private void WriteAtomically(string text)
    {
        var directory = Path.GetDirectoryName(_configPath)
            ?? throw new InvalidOperationException("The Codex config path has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(_configPath))
        {
            var backupPath = _configPath + ".joydex-desktop-bridge.backup";
            if (!File.Exists(backupPath))
            {
                File.Copy(_configPath, backupPath, overwrite: false);
            }
        }

        var temporary = Path.Combine(directory, $".{Path.GetFileName(_configPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool ContainsSection(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(line => string.Equals(line, Section, StringComparison.Ordinal));

    private static (int Start, int End, string? Conflict) FindManagedRange(string text)
    {
        var start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var endMarker = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if ((start < 0) != (endMarker < 0))
        {
            return (-1, -1, "Codex config contains an incomplete Joydex Desktop Task Bridge marker block.");
        }
        if (start < 0)
        {
            return (-1, -1, null);
        }
        if (text.IndexOf(BeginMarker, start + BeginMarker.Length, StringComparison.Ordinal) >= 0
            || text.IndexOf(EndMarker, endMarker + EndMarker.Length, StringComparison.Ordinal) >= 0
            || endMarker < start)
        {
            return (-1, -1, "Codex config contains conflicting Joydex Desktop Task Bridge marker blocks.");
        }

        var end = endMarker + EndMarker.Length;
        if (end < text.Length && text[end] == '\r')
        {
            end++;
        }
        if (end < text.Length && text[end] == '\n')
        {
            end++;
        }
        return (start, end, null);
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
