namespace Joydex.Core.Voice;

public sealed record PebbleIndexPreferences(
    int SchemaVersion = PebbleIndexPreferences.CurrentSchemaVersion,
    bool Enabled = false,
    int Port = PebbleIndexPreferences.DefaultPort,
    string TargetTaskId = "",
    string TargetHostId = "",
    string TargetTaskLabel = "")
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultPort = 5187;
    public const int MinimumPort = 1024;
    public const int MaximumPort = 65535;
    public const int MaximumLabelLength = 120;

    public static PebbleIndexPreferences Default { get; } = new();

    public PebbleIndexPreferences Normalize()
    {
        var targetTaskId = TargetTaskId ?? string.Empty;
        return this with
        {
            TargetTaskId = CodexTaskReference.TryParse(targetTaskId, out var taskId)
                ? taskId
                : targetTaskId.Trim(),
            TargetHostId = (TargetHostId ?? string.Empty).Trim(),
            TargetTaskLabel = (TargetTaskLabel ?? string.Empty).Trim(),
        };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"Unsupported Pebble Index settings schema version {SchemaVersion}.");
        }
        if (Port is < MinimumPort or > MaximumPort)
        {
            errors.Add($"The Pebble Index receiver port must be between {MinimumPort} and {MaximumPort}.");
        }
        if (!string.IsNullOrWhiteSpace(TargetTaskId)
            && !CodexTaskReference.TryParse(TargetTaskId, out _))
        {
            errors.Add("The Pebble Index target must be a Codex task UUID.");
        }
        if (TargetHostId.Any(char.IsControl) || TargetHostId.Length > MaximumLabelLength)
        {
            errors.Add($"The Pebble Index target host must be {MaximumLabelLength} characters or fewer and contain no control characters.");
        }
        if (TargetTaskLabel.Any(char.IsControl) || TargetTaskLabel.Length > MaximumLabelLength)
        {
            errors.Add($"The Pebble Index target label must be {MaximumLabelLength} characters or fewer and contain no control characters.");
        }
        if (string.IsNullOrWhiteSpace(TargetTaskId) != string.IsNullOrWhiteSpace(TargetHostId))
        {
            errors.Add("The Pebble Index target task and host must be selected together.");
        }
        if (Enabled && string.IsNullOrWhiteSpace(TargetTaskId))
        {
            errors.Add("Select a Codex task before enabling the Pebble Index receiver.");
        }
        return errors;
    }
}
