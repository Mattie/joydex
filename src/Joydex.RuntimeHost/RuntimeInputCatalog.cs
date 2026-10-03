using Joydex.Contracts;
using Joydex.Core.Input;
using Joydex.Core.Runtime;

namespace Joydex.RuntimeHost;

internal interface IRuntimeInputCatalog : IAsyncDisposable
{
    RuntimeInputSource[] Refresh(SettingsBundle activeSettings);
    bool ObserveForCapture(Guid captureId, string sourceId);
    void ReleaseCaptureObservation(Guid captureId);
}

internal sealed class SyntheticRuntimeInputCatalog : IRuntimeInputCatalog
{
    private readonly RuntimeInputHost _host;
    private readonly InputSourceDescriptor _descriptor;
    private InputSourceSession? _session;
    private bool _disposed;

    public SyntheticRuntimeInputCatalog(RuntimeInputHost host, string instanceName)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        _descriptor = new InputSourceDescriptor(
            $"synthetic:{instanceName.Trim()}:{Guid.NewGuid():N}",
            $"Synthetic controller ({instanceName.Trim()})",
            "synthetic");
    }

    internal InputSourceSession Session => _session
        ?? throw new InvalidOperationException("The synthetic input catalog has not been refreshed.");

    public RuntimeInputSource[] Refresh(SettingsBundle activeSettings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activeSettings);
        if (_session is null)
        {
            _host.PublishAvailableSources([_descriptor]);
            _session = _host.ConnectSource(_descriptor, static () => { });
        }
        return _host.Sources.Select(source => new RuntimeInputSource(
            source.Descriptor.SourceId,
            source.Descriptor.DisplayName,
            source.Descriptor.HardwareId,
            ProductId: "synthetic",
            ConfiguredDeviceId: source.Descriptor.SourceId,
            source.Generation,
            source.Connected)).ToArray();
    }

    public bool ObserveForCapture(Guid captureId, string sourceId) => !_disposed && string.Equals(
        sourceId,
        _descriptor.SourceId,
        StringComparison.OrdinalIgnoreCase);

    public void ReleaseCaptureObservation(Guid captureId)
    {
    }

    internal Task PublishAsync(
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> events) => _host.RouteAsync(
            Session,
            snapshot,
            events,
            static _ => Task.FromResult(true));

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_session is { } session)
            {
                _host.DisconnectSource(session);
                _session = null;
            }
        }
        return ValueTask.CompletedTask;
    }
}
