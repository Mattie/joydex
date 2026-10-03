using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Runtime;
using Joydex.Windows.Runtime;

namespace Joydex.App;

internal interface IConfigurationInputClient : IDisposable
{
    string ConnectionId { get; }
    event EventHandler<InputObservationEventArgs>? InputObserved;
    event EventHandler<InputCaptureChangedEventArgs>? CaptureChanged;
    IReadOnlyList<RuntimeInputSourceCatalogEntry> RefreshSources();
    InputSourceState? GetSourceState(string sourceId);
    InputCaptureStartResult BeginCapture(string sourceId, string purpose, long? expectedGeneration = null);
    bool ObserveForCapture(string sourceId);
    bool CancelCapture(Guid captureId);
    void ReleaseCaptureObservation(string sourceId);
}

/// <summary>Owns one settings window's input subscriptions and capture leases.</summary>
internal sealed class ConfigurationInputClient : IConfigurationInputClient
{
    private readonly RuntimeInputHost _inputHost;
    private readonly IRuntimeInputSourceProvider _sourceProvider;
    private readonly CompanionConfig _activeConfig;
    private bool _disposed;

    public ConfigurationInputClient(
        RuntimeInputHost inputHost,
        IRuntimeInputSourceProvider sourceProvider,
        CompanionConfig activeConfig,
        string? connectionId = null)
    {
        _inputHost = inputHost ?? throw new ArgumentNullException(nameof(inputHost));
        _sourceProvider = sourceProvider ?? throw new ArgumentNullException(nameof(sourceProvider));
        _activeConfig = CompanionConfigNormalizer.Normalize(activeConfig);
        ConnectionId = string.IsNullOrWhiteSpace(connectionId)
            ? $"settings-{Guid.NewGuid():N}"
            : connectionId.Trim();
        _inputHost.InputObserved += OnInputObserved;
        _inputHost.CaptureChanged += OnCaptureChanged;
    }

    public string ConnectionId { get; }

    public event EventHandler<InputObservationEventArgs>? InputObserved;

    public event EventHandler<InputCaptureChangedEventArgs>? CaptureChanged;

    public IReadOnlyList<RuntimeInputSourceCatalogEntry> RefreshSources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sourceProvider.Refresh(_activeConfig);
    }

    public InputSourceState? GetSourceState(string sourceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inputHost.Sources.FirstOrDefault(source =>
            string.Equals(source.Descriptor.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
    }

    public InputCaptureStartResult BeginCapture(string sourceId, string purpose, long? expectedGeneration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inputHost.BeginCapture(new InputCaptureRequest(
            ConnectionId,
            sourceId,
            purpose,
            expectedGeneration));
    }

    public bool ObserveForCapture(string sourceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sourceProvider.ObserveForCapture(sourceId);
    }

    public bool CancelCapture(Guid captureId) =>
        !_disposed && _inputHost.CancelCapture(captureId, ConnectionId);

    public void ReleaseCaptureObservation(string sourceId)
    {
        if (!_disposed)
        {
            _sourceProvider.ReleaseCaptureObservation(sourceId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inputHost.InputObserved -= OnInputObserved;
        _inputHost.CaptureChanged -= OnCaptureChanged;
        _inputHost.DisconnectClient(ConnectionId);
    }

    private void OnInputObserved(object? sender, InputObservationEventArgs eventArgs)
    {
        if (!_disposed)
        {
            InputObserved?.Invoke(this, eventArgs);
        }
    }

    private void OnCaptureChanged(object? sender, InputCaptureChangedEventArgs eventArgs)
    {
        if (!_disposed
            && string.Equals(eventArgs.Lease.ConnectionId, ConnectionId, StringComparison.Ordinal))
        {
            CaptureChanged?.Invoke(this, eventArgs);
        }
    }
}
