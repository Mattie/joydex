namespace Joydex.Core.TaskAlerts;

public enum TaskAlertState
{
    Running,
    Approval,
    Completed,
    Fault,
}

public enum CodexLifecycleEvent
{
    UserPromptSubmit,
    PermissionRequest,
    Stop,
    Fault,
}

public sealed record TaskAlertEvent(
    CodexLifecycleEvent Event,
    string SessionId,
    string? TurnId,
    DateTimeOffset ReceivedAt);

public sealed record TaskAlertAssignment(
    int Channel,
    string SessionId,
    string? TurnId,
    TaskAlertState State,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompleteAfter = null);

public static class TaskAlertChannels
{
    public static readonly int[] Defaults = [1, 2, 4, 5];

    public static readonly int[] All = [1, 2, 3, 4, 5, 6];

    public static readonly int[] Selectable = [1, 2, 4, 5];

    public static readonly IReadOnlyDictionary<int, int[]> LogicalButtons =
        Enumerable.Range(1, 6).ToDictionary(
            channel => channel,
            channel => new[] { 37 + channel, 55 + channel, 61 + channel, 67 + channel, 73 + channel });

    public static int LedId(int channel) => Validate(channel) + 4;

    public static int? FromLogicalButton(int logicalButton)
    {
        foreach (var pair in LogicalButtons)
        {
            if (Array.IndexOf(pair.Value, logicalButton) >= 0)
            {
                return pair.Key;
            }
        }

        return null;
    }

    public static int Validate(int channel)
    {
        if (channel is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "Task-alert channels are B1 through B6.");
        }

        return channel;
    }

    public static int ValidateSelectable(int channel)
    {
        Validate(channel);
        if (channel is 3 or 6)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "B3 and B6 are reserved for profile bank indication.");
        }

        return channel;
    }
}

public static class TaskAlertColors
{
    public static (byte Red, byte Green, byte Blue) Get(TaskAlertState state) => state switch
    {
        TaskAlertState.Running => (0xFF, 0xFF, 0xFF),
        TaskAlertState.Approval => (0xFF, 0xFF, 0x00),
        TaskAlertState.Completed => (0x00, 0x40, 0x00),
        TaskAlertState.Fault => (0xFF, 0x00, 0x00),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
