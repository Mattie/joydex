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
    UserInputRequest,
    ToolCompleted,
    Stop,
    Fault,
}

public sealed record TaskAlertEvent(
    CodexLifecycleEvent Event,
    string SessionId,
    string? TurnId,
    DateTimeOffset ReceivedAt,
    string? AttentionKey = null);

public sealed record TaskAlertAssignment(
    int Slot,
    string SessionId,
    string? TurnId,
    TaskAlertState State,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompleteAfter = null);

/// <summary>
/// Captures the task assignments and pending approval correlations that must
/// survive a Joydex restart.
/// </summary>
public sealed record TaskAlertPoolState(TaskAlertStoredAssignment[] Assignments)
{
    public static TaskAlertPoolState Empty { get; } = new([]);
}

/// <summary>Represents one assignment and its restart-safe approval state.</summary>
public sealed record TaskAlertStoredAssignment(
    int Slot,
    string SessionId,
    string? TurnId,
    TaskAlertState State,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompleteAfter,
    TaskAlertCorrelationCount[] CorrelatedAttention,
    int UncorrelatedAttentionCount);

/// <summary>Stores one privacy-preserving correlation hash and its pending count.</summary>
public sealed record TaskAlertCorrelationCount(string Key, int Count);

public enum TaskAlertPage
{
    Primary,
    Overflow,
}

public static class TaskAlertSlots
{
    public static readonly int[] Primary = [1, 2, 3, 4];

    public static readonly int[] Overflow = [5, 6, 7, 8, 9, 10];

    public static readonly int[] All = [.. Primary, .. Overflow];

    public static int Validate(int slot)
    {
        if (slot is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "Task-alert slots are 1 through 10.");
        }

        return slot;
    }

    public static TaskAlertPage Page(int slot) => Validate(slot) <= 4
        ? TaskAlertPage.Primary
        : TaskAlertPage.Overflow;

    public static int PageIndex(int slot)
    {
        Validate(slot);
        return slot <= 4 ? slot : slot - 4;
    }

    public static int Button(int slot) => Validate(slot) switch
    {
        1 => 1,
        2 => 2,
        3 => 4,
        4 => 5,
        _ => slot - 4,
    };

    public static int[] Banks(int slot) => Page(slot) == TaskAlertPage.Primary
        ? [2, 3, 4]
        : [1];

    public static int[] LogicalButtons(int slot)
    {
        var button = Button(slot);
        return Banks(slot).Select(bank => LogicalButton(bank, button)).ToArray();
    }

    public static int? FromLogicalButton(int logicalButton)
    {
        foreach (var bank in Enumerable.Range(1, 5))
        {
            var button = logicalButton - BankBase(bank);
            if (button is < 1 or > 6)
            {
                continue;
            }

            return FromBankAndButton(bank, button);
        }

        return null;
    }

    public static int? FromBankAndButton(int bank, int button)
    {
        if (button is < 1 or > 6)
        {
            return null;
        }

        return bank switch
        {
            1 => 4 + button,
            2 or 3 or 4 => button switch
            {
                1 => 1,
                2 => 2,
                4 => 3,
                5 => 4,
                _ => null,
            },
            _ => null,
        };
    }

    public static int BankFromLogicalButton(int logicalButton) => logicalButton switch
    {
        >= 38 and <= 43 => 1,
        >= 56 and <= 61 => 2,
        >= 62 and <= 67 => 3,
        >= 68 and <= 73 => 4,
        >= 74 and <= 79 => 5,
        _ => 0,
    };

    private static int LogicalButton(int bank, int button) => BankBase(bank) + button;

    private static int BankBase(int bank) => bank switch
    {
        1 => 37,
        2 => 55,
        3 => 61,
        4 => 67,
        5 => 73,
        _ => throw new ArgumentOutOfRangeException(nameof(bank)),
    };
}

public static class TaskAlertColors
{
    public static (byte Red, byte Green, byte Blue) Get(TaskAlertState state) => state switch
    {
        TaskAlertState.Running => (0x55, 0x55, 0x55),
        TaskAlertState.Approval => (0xFF, 0xFF, 0x00),
        TaskAlertState.Completed => (0x00, 0x40, 0x00),
        TaskAlertState.Fault => (0xFF, 0x00, 0x00),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
