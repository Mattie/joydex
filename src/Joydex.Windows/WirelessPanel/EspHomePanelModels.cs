namespace Joydex.Windows.WirelessPanel;

/// <summary>Identifies one of the fixed touch targets exposed by the ESPHome panel.</summary>
public enum EspHomePanelButton
{
    Task1 = 1,
    Task2 = 2,
    Task3 = 3,
    Task4 = 4,
    PlanMode = 5,
    FastMode = 6,
    SideChat = 7,
    VoiceMute = 8,
    Approve = 9,
    Reject = 10,
    NewTask = 11,
    ForkTask = 12,
    PreviousTask = 13,
    Submit = 14,
    NextTask = 15,
}

/// <summary>Represents the coarse task state rendered by one panel card.</summary>
public enum EspHomeTaskState
{
    Empty,
    Running,
    Attention,
    Complete,
}
