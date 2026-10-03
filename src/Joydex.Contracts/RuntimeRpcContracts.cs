namespace Joydex.Contracts;

/// <summary>
/// The request surface exposed by one runtime connection. The transport binds each implementation
/// to a host-issued connection identity, so callers never submit or select another connection ID.
/// </summary>
public interface IRuntimeRpcServer
{
    Task<RuntimeAttachResult> AttachAsync(
        RuntimeAttachRequest request,
        CancellationToken cancellationToken);

    Task<RuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<PrepareSettingsResult> PrepareSettingsAsync(
        PrepareSettingsRequest request,
        CancellationToken cancellationToken);

    Task<ApplySettingsResult> ApplySettingsAsync(
        ApplySettingsRequest request,
        CancellationToken cancellationToken);

    Task<SettingsOperationResult> GetSettingsOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(CancellationToken cancellationToken);

    Task<RuntimeCaptureStartResult> BeginInputCaptureAsync(
        RuntimeCaptureRequest request,
        CancellationToken cancellationToken);

    Task<RuntimeCaptureCommandResult> RenewInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken);

    Task<RuntimeCaptureCommandResult> CancelInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken);

    Task<RuntimeCaptureLookupResult> GetInputCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken);

    Task<RuntimeCommandResult> ExecuteCommandAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken);

    Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}

/// <summary>The callback surface implemented by an attached runtime client.</summary>
public interface IRuntimeRpcClient
{
    Task RuntimeEventAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken);

    Task RuntimeInputEventAsync(
        RuntimeConnectionInputEvent inputEvent,
        CancellationToken cancellationToken);

    Task RuntimeCommandCompletedAsync(
        RuntimeCommandResult result,
        CancellationToken cancellationToken);
}
