using Joydex.Contracts;
using Joydex.Core.Runtime;
using Joydex.RuntimeHost.Settings;

namespace Joydex.RuntimeHost;

/// <summary>
/// Owns the hardware and external-service adapters for one runtime generation.
/// The engine creates exactly one composition after it has acquired process ownership.
/// </summary>
internal interface IRuntimeComposition :
    IRuntimeInputCatalog,
    ISettingsActivator,
    IRuntimeCommandHandler
{
    bool VoiceSessionActive { get; }
    event EventHandler? ActivationBoundaryAvailable;
    event EventHandler<RuntimeUiEvent>? UiChanged;
    RuntimeUiSnapshot GetUiSnapshot();
}

internal sealed class RuntimeUiEvent(
    RuntimeEventKind kind,
    RuntimePromptPickerSnapshot? promptPicker = null,
    RuntimeButtonMapVisibility? buttonMapVisibility = null,
    RuntimeControllerStatus? controllerStatus = null,
    RuntimeActionActivity? actionActivity = null,
    RuntimeTaskAlertSnapshot? taskAlerts = null,
    RuntimeVoiceEvent? voice = null,
    RuntimePebbleIndexSnapshot? pebbleIndex = null) : EventArgs
{
    public RuntimeEventKind Kind { get; } = kind;
    public RuntimePromptPickerSnapshot? PromptPicker { get; } = promptPicker;
    public RuntimeButtonMapVisibility? ButtonMapVisibility { get; } = buttonMapVisibility;
    public RuntimeControllerStatus? ControllerStatus { get; } = controllerStatus;
    public RuntimeActionActivity? ActionActivity { get; } = actionActivity;
    public RuntimeTaskAlertSnapshot? TaskAlerts { get; } = taskAlerts;
    public RuntimeVoiceEvent? Voice { get; } = voice;
    public RuntimePebbleIndexSnapshot? PebbleIndex { get; } = pebbleIndex;
}

internal sealed class SyntheticRuntimeComposition : IRuntimeComposition
{
    private readonly SyntheticRuntimeInputCatalog _inputs;
    private readonly SyntheticRuntimeCommandHandler _commands = new();

    public SyntheticRuntimeComposition(RuntimeInputHost host, string instanceName)
    {
        _inputs = new SyntheticRuntimeInputCatalog(host, instanceName);
    }

    internal SyntheticRuntimeInputCatalog Inputs => _inputs;

    public bool VoiceSessionActive => false;

    public event EventHandler? ActivationBoundaryAvailable
    {
        add { }
        remove { }
    }

    public event EventHandler<RuntimeUiEvent>? UiChanged
    {
        add { }
        remove { }
    }

    public RuntimeUiSnapshot GetUiSnapshot() => new();

    public RuntimeInputSource[] Refresh(SettingsBundle activeSettings) =>
        _inputs.Refresh(activeSettings);

    public bool ObserveForCapture(Guid captureId, string sourceId) =>
        _inputs.ObserveForCapture(captureId, sourceId);

    public void ReleaseCaptureObservation(Guid captureId) =>
        _inputs.ReleaseCaptureObservation(captureId);

    public Task<SettingsActivationResult> ActivateAsync(
        SettingsAggregateId aggregate,
        SettingsBundle activationCandidate,
        long desiredRevision,
        CancellationToken runtimeCancellationToken)
    {
        runtimeCancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SettingsActivationResult(SettingsActivationState.Applied));
    }

    public Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken) =>
        _commands.ExecuteAsync(request, runtimeCancellationToken);

    public ValueTask DisposeAsync() => _inputs.DisposeAsync();
}
