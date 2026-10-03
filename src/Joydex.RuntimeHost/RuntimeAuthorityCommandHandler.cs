using Joydex.Contracts;
using Joydex.RuntimeHost.Settings;

namespace Joydex.RuntimeHost;

/// <summary>
/// Keeps process and settings authority commands inside the engine while forwarding feature
/// commands to the active production or synthetic composition.
/// </summary>
internal sealed class RuntimeAuthorityCommandHandler(
    RuntimeSettingsCoordinator settings,
    IRuntimeCommandHandler composition,
    IRuntimeSettingsProcessLauncher settingsProcessLauncher) : IRuntimeCommandRouter
{
    public async Task<RuntimeCommandResult> ExecuteAsync(
        string connectionId,
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken)
    {
        switch (request.Kind)
        {
            case RuntimeCommandKind.ReloadConfiguration:
                return await AdoptExternalAsync(
                    connectionId,
                    request,
                    requestedAggregates: null,
                    runtimeCancellationToken).ConfigureAwait(false);
            case RuntimeCommandKind.AdoptExternalSettings:
                return await AdoptExternalAsync(
                    connectionId,
                    request,
                    request.Arguments!.ExternalSettingsAggregates,
                    runtimeCancellationToken).ConfigureAwait(false);
            case RuntimeCommandKind.OpenSettings:
                return await settingsProcessLauncher
                    .OpenAsync(request, runtimeCancellationToken)
                    .ConfigureAwait(false);
            case RuntimeCommandKind.ShutdownRuntime:
                return new RuntimeCommandResult(
                    request.OperationId,
                    request.Kind,
                    RuntimeCommandStatus.Completed,
                    "Runtime shutdown was accepted.");
            default:
                return await composition.ExecuteAsync(request, runtimeCancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private async Task<RuntimeCommandResult> AdoptExternalAsync(
        string connectionId,
        RuntimeCommandRequest request,
        IReadOnlyCollection<SettingsAggregateId>? requestedAggregates,
        CancellationToken cancellationToken)
    {
        var adopted = await settings.AdoptExternalAsync(
                connectionId,
                requestedAggregates,
                cancellationToken)
            .ConfigureAwait(false);
        return new RuntimeCommandResult(
            request.OperationId,
            request.Kind,
            adopted.Status,
            adopted.Detail,
            new RuntimeCommandPayload(Settings: adopted.Snapshot));
    }
}
