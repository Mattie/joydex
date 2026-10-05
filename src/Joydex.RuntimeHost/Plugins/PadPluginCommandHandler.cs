using Joydex.Contracts;

namespace Joydex.RuntimeHost.Plugins;

/// <summary>Dispatches the three typed management commands for the bundled PAD.</summary>
internal sealed class PadPluginCommandHandler(
    PadPlugin pad,
    Action<string> log,
    Func<BundledPluginHealth>? voiceHealth = null)
{
    private readonly PadPlugin _pad = pad ?? throw new ArgumentNullException(nameof(pad));
    private readonly Action<string> _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly Func<BundledPluginHealth> _voiceHealth = voiceHealth ?? (() => new(
        BundledPluginCatalog.VoiceId,
        BundledPluginLifecycleState.Disabled,
        0,
        "Room Voice is disabled.",
        false,
        false));

    public async Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == RuntimeCommandKind.InspectPlugins)
        {
            return Result(request, RuntimeCommandStatus.Completed);
        }

        var pluginId = request.Arguments?.PluginId;
        if (string.Equals(pluginId, BundledPluginCatalog.VoiceId, StringComparison.Ordinal))
        {
            return Result(
                request,
                RuntimeCommandStatus.Rejected,
                "Room Voice is managed by its existing Voice controls.");
        }
        if (string.IsNullOrEmpty(pluginId)
            || !string.Equals(pluginId, BundledPluginCatalog.PadId, StringComparison.Ordinal))
        {
            return Result(
                request,
                RuntimeCommandStatus.Rejected,
                "The requested bundled plugin is not registered.");
        }

        var canRun = request.Kind switch
        {
            RuntimeCommandKind.RestartPlugin => _pad.Health.CanRestart,
            RuntimeCommandKind.ReloadPluginConfiguration => _pad.Health.CanReload,
            _ => false,
        };
        if (!canRun)
        {
            return Result(
                request,
                RuntimeCommandStatus.Rejected,
                request.Kind == RuntimeCommandKind.RestartPlugin
                    ? "PAD restart is unavailable in its current state."
                    : "PAD configuration reload is unavailable in its current state.");
        }

        try
        {
            if (request.Kind == RuntimeCommandKind.RestartPlugin)
            {
                await _pad.RestartAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _pad.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
            return Result(request, RuntimeCommandStatus.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            var status = _pad.Health.State is
                BundledPluginLifecycleState.Faulted
                or BundledPluginLifecycleState.Blocked
                ? RuntimeCommandStatus.Failed
                : RuntimeCommandStatus.Rejected;
            return Result(request, status, exception.Message);
        }
        catch (Exception exception)
        {
            try
            {
                _log(
                    $"{BundledPluginCatalog.PadId}: plugin command failed "
                    + $"({exception.GetType().Name}).");
            }
            catch
            {
            }
            return Result(
                request,
                RuntimeCommandStatus.Failed,
                "The PAD plugin command failed.");
        }
    }

    private RuntimeCommandResult Result(
        RuntimeCommandRequest request,
        RuntimeCommandStatus status,
        string? detail = null) => new(
        request.OperationId,
        request.Kind,
        status,
        detail,
        new RuntimeCommandPayload(Plugins: new RuntimePluginSnapshot(
            BundledPluginCatalog.Registrations.Select(MapRegistration).ToArray(),
            [MapHealth(_pad.Health), MapHealth(_voiceHealth())])));

    private static RuntimePluginRegistration MapRegistration(
        BundledPluginRegistration registration) => new(
        registration.Id,
        registration.Version,
        registration.HostApiMajor,
        registration.MinimumHostApiMinor,
        registration.SettingsSchemaVersion,
        registration.Execution == BundledPluginExecutionModel.InProcess);

    private static RuntimePluginHealth MapHealth(BundledPluginHealth health) => new(
        health.PluginId,
        health.State switch
        {
            BundledPluginLifecycleState.Disabled => RuntimePluginState.Disabled,
            BundledPluginLifecycleState.Starting => RuntimePluginState.Starting,
            BundledPluginLifecycleState.Ready => RuntimePluginState.Ready,
            BundledPluginLifecycleState.Retrying => RuntimePluginState.Retrying,
            BundledPluginLifecycleState.Blocked => RuntimePluginState.Blocked,
            BundledPluginLifecycleState.Faulted => RuntimePluginState.Faulted,
            BundledPluginLifecycleState.Stopping => RuntimePluginState.Stopping,
            BundledPluginLifecycleState.Stopped => RuntimePluginState.Stopped,
            _ => throw new ArgumentOutOfRangeException(nameof(health)),
        },
        health.Generation,
        health.Detail,
        health.CanRestart,
        health.CanReload);
}
