namespace Joydex.Contracts;

/// <summary>Stable IDs for the trusted plugins bundled into this Joydex release.</summary>
public static class RuntimePluginIds
{
    public const string Pad = "joydex.pad";
    public const string VoicePe = "joydex.voice-pe";
    public const string PebbleIndex = "joydex.pebble-index";
}

/// <summary>Wire limits for plugin identity and health presented by bundled clients.</summary>
public static class RuntimePluginLimits
{
    public const int MaximumPluginIdCharacters = 64;
    public const int MaximumHealthDetailCharacters = 256;
    public const int MaximumBundledPlugins = 32;

    /// <summary>
    /// Checks a publisher-qualified, lowercase ID without trimming or case folding. Command
    /// callers must address the catalog entry exactly.
    /// </summary>
    public static bool IsCanonicalPluginId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaximumPluginIdCharacters)
        {
            return false;
        }

        var segments = value.Split('.');
        return segments.Length >= 2 && segments.All(IsCanonicalSegment);
    }

    private static bool IsCanonicalSegment(string segment)
    {
        if (segment.Length == 0
            || segment[0] == '-'
            || segment[^1] == '-')
        {
            return false;
        }

        var previousWasDash = false;
        foreach (var character in segment)
        {
            if (character == '-')
            {
                if (previousWasDash)
                {
                    return false;
                }
                previousWasDash = true;
                continue;
            }
            if (character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9'))
            {
                return false;
            }
            previousWasDash = false;
        }
        return true;
    }
}

public enum RuntimePluginState
{
    Disabled,
    Starting,
    Ready,
    Retrying,
    Blocked,
    Faulted,
    Stopping,
    Stopped,
}

/// <summary>Non-sensitive metadata from the explicit bundled plugin catalog.</summary>
public sealed record RuntimePluginRegistration(
    string Id,
    string Version,
    int HostApiMajor,
    int MinimumHostApiMinor,
    int SettingsSchemaVersion,
    bool ExecutionInProcess);

/// <summary>
/// Bounded operational state. Detail contains a fixed sanitized sentence and never includes
/// exception text, configuration contents, credentials, endpoints, or filesystem paths.
/// </summary>
public sealed record RuntimePluginHealth(
    string PluginId,
    RuntimePluginState State,
    long Generation,
    string Detail,
    bool CanRestart,
    bool CanReload);

/// <summary>A small point-in-time catalog and health inspection returned by typed commands.</summary>
public sealed record RuntimePluginSnapshot(
    RuntimePluginRegistration[] Registrations,
    RuntimePluginHealth[] Health);
