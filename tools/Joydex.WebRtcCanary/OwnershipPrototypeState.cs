namespace Joydex.WebRtcCanary;

internal enum OwnershipPrototypeStage
{
    Ready,
    OwnerRunning,
    TaskOwned,
    RivalRejected,
    OwnerReleased,
    HandoffAcquired,
    CanaryDeleted,
    TargetProbed,
    Failed,
}

internal sealed record OwnershipPrototypeState(
    OwnershipPrototypeStage Stage,
    string LastAction,
    int? OwnerProcessId = null,
    string? CanaryThreadId = null,
    string? RivalResult = null,
    string? TargetResult = null)
{
    public static OwnershipPrototypeState Initial { get; } = new(
        OwnershipPrototypeStage.Ready,
        "Ready to test process-owned task writer lifetime.");

    public OwnershipPrototypeState Apply(OwnershipPrototypeEvent prototypeEvent) => prototypeEvent switch
    {
        ProofReset reset => Initial with
        {
            LastAction = reset.Message,
        },
        OwnerStarted owner => this with
        {
            Stage = OwnershipPrototypeStage.OwnerRunning,
            LastAction = "Joydex launched and retained its App Server process.",
            OwnerProcessId = owner.ProcessId,
            CanaryThreadId = null,
            RivalResult = null,
        },
        TaskAcquired task => this with
        {
            Stage = OwnershipPrototypeStage.TaskOwned,
            LastAction = "The Joydex-owned App Server created and owns the disposable durable task.",
            CanaryThreadId = task.ThreadId,
        },
        RivalBlocked rival => this with
        {
            Stage = OwnershipPrototypeStage.RivalRejected,
            LastAction = "A separate App Server was rejected by the active writer lock.",
            RivalResult = rival.Message,
        },
        OwnerStopped => this with
        {
            Stage = OwnershipPrototypeStage.OwnerReleased,
            LastAction = "Stopping the Joydex-owned App Server released its writer.",
            OwnerProcessId = null,
        },
        HandoffSucceeded => this with
        {
            Stage = OwnershipPrototypeStage.HandoffAcquired,
            LastAction = "A fresh App Server resumed the same task after owner shutdown.",
        },
        CanaryTaskDeleted => this with
        {
            Stage = OwnershipPrototypeStage.CanaryDeleted,
            LastAction = "The uniquely identified disposable canary task was deleted.",
            CanaryThreadId = null,
        },
        ConfiguredTargetProbed target => this with
        {
            Stage = OwnershipPrototypeStage.TargetProbed,
            LastAction = "The configured Dedicated Voice Task ownership probe completed.",
            TargetResult = target.Message,
        },
        PrototypeFailed failed => this with
        {
            Stage = OwnershipPrototypeStage.Failed,
            LastAction = failed.Message,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(prototypeEvent)),
    };
}

internal abstract record OwnershipPrototypeEvent;

internal sealed record ProofReset(string Message) : OwnershipPrototypeEvent;

internal sealed record OwnerStarted(int ProcessId) : OwnershipPrototypeEvent;

internal sealed record TaskAcquired(string ThreadId) : OwnershipPrototypeEvent;

internal sealed record RivalBlocked(string Message) : OwnershipPrototypeEvent;

internal sealed record OwnerStopped : OwnershipPrototypeEvent;

internal sealed record HandoffSucceeded : OwnershipPrototypeEvent;

internal sealed record CanaryTaskDeleted : OwnershipPrototypeEvent;

internal sealed record ConfiguredTargetProbed(string Message) : OwnershipPrototypeEvent;

internal sealed record PrototypeFailed(string Message) : OwnershipPrototypeEvent;
