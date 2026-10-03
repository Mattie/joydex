using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Runtime;
using Joydex.Windows.Input;

namespace Joydex.Windows.Runtime;

public sealed record RuntimeInputSourceCatalogEntry(
    InputSourceDescriptor Source,
    DeviceSelector Selector,
    Guid InstanceGuid,
    Guid ProductGuid,
    string? ConfiguredDeviceId);

/// <summary>Supplies attached controllers without giving a settings window an acquisition handle.</summary>
public interface IRuntimeInputSourceProvider : IAsyncDisposable
{
    IReadOnlyList<RuntimeInputSourceCatalogEntry> Refresh(CompanionConfig activeConfig);

    bool ObserveForCapture(string sourceId);

    void ReleaseCaptureObservation(string sourceId);
}

/// <summary>
/// Publishes the DirectInput catalog and temporarily observes unconfigured devices during capture.
/// </summary>
public sealed class RuntimeInputSourceProvider : IRuntimeInputSourceProvider
{
    private readonly object _gate = new();
    private readonly RuntimeInputHost _inputHost;
    private readonly IJoystickSourceFactory _sourceFactory;
    private readonly PollingOptions _polling;
    private readonly Action<string> _log;
    private readonly Dictionary<string, RuntimeInputSourceCatalogEntry> _catalog =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeInputObservationWorker> _observers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _retiredObserverDisposals = [];
    private bool _disposed;

    public RuntimeInputSourceProvider(
        RuntimeInputHost inputHost,
        IJoystickSourceFactory sourceFactory,
        PollingOptions polling,
        Action<string> log)
    {
        _inputHost = inputHost ?? throw new ArgumentNullException(nameof(inputHost));
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _polling = polling ?? throw new ArgumentNullException(nameof(polling));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<RuntimeInputSourceCatalogEntry> Refresh(CompanionConfig activeConfig)
    {
        ArgumentNullException.ThrowIfNull(activeConfig);
        var config = CompanionConfigNormalizer.Normalize(activeConfig);
        var attached = _sourceFactory.EnumerateDevices();
        var profileByInstance = MatchProfiles(config.Devices, attached);
        var refreshed = attached.Select(device =>
        {
            profileByInstance.TryGetValue(device.InstanceGuid, out var profile);
            var sourceId = profile?.Id ?? $"directinput:{device.InstanceGuid:D}";
            var selector = new DeviceSelector
            {
                ProductNameContains = device.ProductName,
                InstanceGuid = device.InstanceGuid.ToString("D"),
                ProductGuid = device.ProductGuid.ToString("D"),
            };
            return new RuntimeInputSourceCatalogEntry(
                new InputSourceDescriptor(sourceId, device.ProductName, device.InstanceGuid.ToString("D")),
                selector,
                device.InstanceGuid,
                device.ProductGuid,
                profile?.Id);
        }).ToArray();

        lock (_gate)
        {
            ThrowIfDisposed();
            _catalog.Clear();
            foreach (var entry in refreshed)
            {
                _catalog[entry.Source.SourceId] = entry;
            }
        }

        _inputHost.PublishAvailableSources(refreshed.Select(entry => entry.Source));
        return refreshed;
    }

    public bool ObserveForCapture(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        RuntimeInputObservationWorker? observer = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_catalog.TryGetValue(sourceId, out var entry))
            {
                return false;
            }

            if (entry.ConfiguredDeviceId is not null)
            {
                // Its ordinary CompanionWorker already owns acquisition and reconnect.
                return true;
            }

            if (_observers.TryGetValue(sourceId, out var existing) && !existing.IsCompleted)
            {
                return true;
            }

            if (existing is not null)
            {
                _retiredObserverDisposals.Add(existing.DisposeAsync().AsTask());
            }

            observer = new RuntimeInputObservationWorker(
                _inputHost,
                _sourceFactory.Create(),
                entry,
                _polling,
                _log);
            _observers[sourceId] = observer;
        }

        observer.Start();
        return true;
    }

    public void ReleaseCaptureObservation(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        RuntimeInputObservationWorker? observer;
        lock (_gate)
        {
            if (_disposed || !_observers.Remove(sourceId, out observer))
            {
                return;
            }
        }
        observer.Stop();
        lock (_gate)
        {
            _retiredObserverDisposals.Add(observer.DisposeAsync().AsTask());
        }
    }

    public async ValueTask DisposeAsync()
    {
        RuntimeInputObservationWorker[] observers;
        Task[] retiredDisposals;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            observers = _observers.Values.ToArray();
            _observers.Clear();
            _catalog.Clear();
            retiredDisposals = _retiredObserverDisposals.ToArray();
            _retiredObserverDisposals.Clear();
        }

        foreach (var observer in observers)
        {
            observer.Stop();
        }
        foreach (var observer in observers)
        {
            await observer.DisposeAsync().ConfigureAwait(false);
        }
        await Task.WhenAll(retiredDisposals).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static Dictionary<Guid, DeviceProfile> MatchProfiles(
        IReadOnlyList<DeviceProfile> profiles,
        IReadOnlyList<DirectInputDeviceInfo> attached)
    {
        var matches = new Dictionary<Guid, DeviceProfile>();
        var assignedProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            if (!Guid.TryParse(profile.Selector.InstanceGuid, out var instanceGuid))
            {
                continue;
            }
            var device = attached.FirstOrDefault(candidate => candidate.InstanceGuid == instanceGuid);
            if (device is not null && DirectInputJoystickSource.Matches(device, profile.Selector))
            {
                matches[device.InstanceGuid] = profile;
                assignedProfiles.Add(profile.Id);
            }
        }

        foreach (var profile in profiles.Where(profile => !assignedProfiles.Contains(profile.Id)))
        {
            var device = attached.FirstOrDefault(candidate =>
                !matches.ContainsKey(candidate.InstanceGuid)
                && DirectInputJoystickSource.Matches(candidate, profile.Selector));
            if (device is not null)
            {
                matches[device.InstanceGuid] = profile;
            }
        }
        return matches;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class RuntimeInputObservationWorker : IAsyncDisposable
{
    private readonly RuntimeInputHost _inputHost;
    private readonly IJoystickSource _source;
    private readonly RuntimeInputSourceCatalogEntry _catalogEntry;
    private readonly PollingOptions _polling;
    private readonly Action<string> _log;
    private readonly CompanionEngine _engine;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _runTask;
    private InputSourceSession? _session;

    public RuntimeInputObservationWorker(
        RuntimeInputHost inputHost,
        IJoystickSource source,
        RuntimeInputSourceCatalogEntry catalogEntry,
        PollingOptions polling,
        Action<string> log)
    {
        _inputHost = inputHost;
        _source = source;
        _catalogEntry = catalogEntry;
        _polling = polling;
        _log = log;
        _engine = new CompanionEngine(new CompanionConfig
        {
            Devices =
            [
                new DeviceProfile
                {
                    Id = catalogEntry.Source.SourceId,
                    DisplayName = catalogEntry.Source.DisplayName,
                    Selector = catalogEntry.Selector,
                },
            ],
            Polling = polling,
        }, deviceId: catalogEntry.Source.SourceId);
    }

    public bool IsCompleted => _runTask?.IsCompleted ?? false;

    public void Start() => _runTask ??= RunAsync(_cancellation.Token);

    public void Stop() => _cancellation.Cancel();

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cancellation.Dispose();
        _source.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_source.ConnectedDevice is null)
                {
                    if (!_source.TryConnect(_catalogEntry.Selector, out var message))
                    {
                        _log(message);
                        await Task.Delay(_polling.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _engine.Reset();
                    InputSourceSession session = default;
                    session = _inputHost.ConnectSource(
                        _catalogEntry.Source,
                        () => _engine.ResetDispatchState());
                    _session = session;
                    await Task.Delay(_polling.ConnectWarmupMs, cancellationToken).ConfigureAwait(false);
                    if (_source.TryRead(out var baseline, out _) && baseline is not null)
                    {
                        await ObserveAsync(baseline, [], session).ConfigureAwait(false);
                    }
                    continue;
                }

                if (!_source.TryRead(out var snapshot, out var error) || snapshot is null)
                {
                    DisconnectSession();
                    _engine.Reset();
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        _log($"DirectInput capture observer disconnected: {error}");
                    }
                    await Task.Delay(_polling.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_session is { } currentSession)
                {
                    await ObserveAsync(snapshot, _source.LatestBufferedButtonEvents, currentSession).ConfigureAwait(false);
                }
                await Task.Delay(_polling.PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log($"Input capture observer stopped unexpectedly: {exception.Message}");
        }
        finally
        {
            DisconnectSession();
            _source.Disconnect();
        }
    }

    private Task<bool> ObserveAsync(
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> bufferedEvents,
        InputSourceSession session)
    {
        var events = _engine.Observe(snapshot, bufferedEvents);
        return _inputHost.RouteAsync(session, snapshot, events, _ => Task.FromResult(true));
    }

    private void DisconnectSession()
    {
        if (_session is not { } session)
        {
            return;
        }
        _inputHost.DisconnectSource(session);
        _session = null;
    }
}
