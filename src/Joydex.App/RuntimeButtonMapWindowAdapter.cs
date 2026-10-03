using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;

namespace Joydex.App;

internal interface IRuntimeButtonMapViewFactory
{
    IRuntimeButtonMapView Create(CompanionConfig config, string deviceId, string statePath);
}

internal interface IRuntimeButtonMapView : IDisposable
{
    event EventHandler? HiddenByUser;

    void UpdateConfig(CompanionConfig config);

    void UpdateTaskAlerts(
        IReadOnlyList<TaskAlertAssignment> assignments,
        TaskAlertLedOptions? ledOptions);

    void ShowReference();

    void HideReference();
}

/// <summary>Owns local button-map windows while runtime state remains authoritative.</summary>
internal sealed class RuntimeButtonMapWindowAdapter : IDisposable
{
    private readonly object _gate = new();
    private readonly SynchronizationContext _ui;
    private readonly Func<string, string> _statePathForDevice;
    private readonly IRuntimeButtonMapViewFactory _viewFactory;
    private readonly Dictionary<string, DeviceWindow> _devices = new(StringComparer.OrdinalIgnoreCase);
    private CompanionConfig? _activeConfig;
    private RuntimeTaskAlertSnapshot? _taskAlerts;
    private bool _disposed;

    public RuntimeButtonMapWindowAdapter(
        SynchronizationContext ui,
        Func<string, string> statePathForDevice,
        IRuntimeButtonMapViewFactory? viewFactory = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _statePathForDevice = statePathForDevice
            ?? throw new ArgumentNullException(nameof(statePathForDevice));
        _viewFactory = viewFactory ?? new ButtonMapViewFactory();
    }

    public void ApplySnapshot(
        CompanionConfig active,
        RuntimeTaskAlertSnapshot? alerts,
        RuntimeControllerStatus[]? controllers,
        RuntimeButtonMapVisibility[]? visibility)
    {
        ArgumentNullException.ThrowIfNull(active);
        var normalized = CompanionConfigNormalizer.Normalize(active);
        var controllerStates = (controllers ?? [])
            .GroupBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var mappedVisibility = (visibility ?? [])
            .GroupBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Visible, StringComparer.OrdinalIgnoreCase);
        Post(() => ApplySnapshotOnUi(normalized, alerts, controllerStates, mappedVisibility));
    }

    public void ApplyVisibility(RuntimeButtonMapVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        Post(() =>
        {
            if (_devices.TryGetValue(visibility.DeviceId, out var device))
            {
                ApplyMappedVisibility(device, visibility.Visible);
            }
        });
    }

    public void ApplyController(RuntimeControllerStatus controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        Post(() =>
        {
            if (!_devices.TryGetValue(controller.DeviceId, out var device))
            {
                return;
            }
            device.Eligible = controller.HasButtonMap && HasConfiguredMap(device.Profile);
            if (!device.Eligible)
            {
                DisposeView(device);
            }
            else
            {
                ReconcileVisibility(device);
            }
        });
    }

    public void ApplyTaskAlerts(RuntimeTaskAlertSnapshot? alerts)
    {
        Post(() =>
        {
            _taskAlerts = alerts;
            foreach (var device in _devices.Values)
            {
                UpdateTaskAlerts(device);
            }
        });
    }

    public void ShowManual(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        Post(() =>
        {
            if (_devices.TryGetValue(deviceId, out var device))
            {
                device.ManualVisible = true;
                device.MappedSuppressedUntilFalse = false;
                ReconcileVisibility(device);
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        if (ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            DisposeOnUi();
        }
        else
        {
            _ui.Send(_ => DisposeOnUi(), null);
        }
    }

    private void ApplySnapshotOnUi(
        CompanionConfig active,
        RuntimeTaskAlertSnapshot? alerts,
        IReadOnlyDictionary<string, RuntimeControllerStatus> controllers,
        IReadOnlyDictionary<string, bool> visibility)
    {
        _activeConfig = active;
        _taskAlerts = alerts;
        var configured = active.Devices
            .Where(HasConfiguredMap)
            .ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var removed in _devices.Keys.Where(id => !configured.ContainsKey(id)).ToArray())
        {
            DisposeView(_devices[removed]);
            _devices.Remove(removed);
        }

        foreach (var profile in configured.Values)
        {
            if (!_devices.TryGetValue(profile.Id, out var device))
            {
                device = new DeviceWindow(profile);
                _devices.Add(profile.Id, device);
            }
            else if (RequiresRecreation(device.Profile, profile))
            {
                DisposeView(device);
            }

            device.Profile = profile;
            device.Eligible = !controllers.TryGetValue(profile.Id, out var controller)
                || controller.HasButtonMap;
            if (!device.Eligible)
            {
                DisposeView(device);
            }
            else if (device.View is not null)
            {
                device.View.UpdateConfig(active);
                UpdateTaskAlerts(device);
            }

            ApplyMappedVisibility(
                device,
                visibility.TryGetValue(profile.Id, out var mapped) && mapped);
        }
    }

    private void ApplyMappedVisibility(DeviceWindow device, bool visible)
    {
        device.MappedVisible = visible;
        if (!visible)
        {
            device.MappedSuppressedUntilFalse = false;
        }
        ReconcileVisibility(device);
    }

    private void ReconcileVisibility(DeviceWindow device)
    {
        var shouldShow = device.Eligible
            && (device.ManualVisible
                || (device.MappedVisible && !device.MappedSuppressedUntilFalse));
        if (!shouldShow)
        {
            if (device.RenderedVisible)
            {
                device.View?.HideReference();
                device.RenderedVisible = false;
            }
            return;
        }

        var view = EnsureView(device);
        if (!device.RenderedVisible)
        {
            view.ShowReference();
            device.RenderedVisible = true;
        }
    }

    private IRuntimeButtonMapView EnsureView(DeviceWindow device)
    {
        if (device.View is not null)
        {
            return device.View;
        }
        var active = _activeConfig
            ?? throw new InvalidOperationException("A runtime snapshot is required before showing a button map.");
        var view = _viewFactory.Create(active, device.Profile.Id, _statePathForDevice(device.Profile.Id))
            ?? throw new InvalidOperationException("The button-map view factory returned no view.");
        device.HiddenByUser = (_, _) => OnHiddenByUser(device);
        view.HiddenByUser += device.HiddenByUser;
        device.View = view;
        view.UpdateConfig(active);
        UpdateTaskAlerts(device);
        return view;
    }

    private void UpdateTaskAlerts(DeviceWindow device)
    {
        if (device.View is null)
        {
            return;
        }
        device.View.UpdateTaskAlerts(
            _taskAlerts?.Assignments ?? [],
            _taskAlerts?.LedOutput);
    }

    private void OnHiddenByUser(DeviceWindow device)
    {
        device.RenderedVisible = false;
        device.ManualVisible = false;
        if (device.MappedVisible)
        {
            device.MappedSuppressedUntilFalse = true;
        }
    }

    private void DisposeView(DeviceWindow device)
    {
        var view = device.View;
        if (view is null)
        {
            device.RenderedVisible = false;
            return;
        }

        var failures = new List<Exception>();
        try
        {
            if (device.HiddenByUser is not null)
            {
                view.HiddenByUser -= device.HiddenByUser;
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            if (device.RenderedVisible)
            {
                view.HideReference();
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            view.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        device.View = null;
        device.HiddenByUser = null;
        device.RenderedVisible = false;
        if (failures.Count == 1)
        {
            throw failures[0];
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }

    private void DisposeOnUi()
    {
        var failures = new List<Exception>();
        foreach (var device in _devices.Values)
        {
            try
            {
                DisposeView(device);
            }
            catch (AggregateException exception)
            {
                failures.AddRange(exception.InnerExceptions);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        _devices.Clear();
        _activeConfig = null;
        _taskAlerts = null;
        if (failures.Count == 1)
        {
            throw failures[0];
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }

    private void Post(Action action)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }
        _ui.Post(_ =>
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }
            action();
        }, null);
    }

    private static bool HasConfiguredMap(DeviceProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.Id)
        && !string.IsNullOrWhiteSpace(profile.ButtonMapTemplate);

    private static bool RequiresRecreation(DeviceProfile previous, DeviceProfile current) =>
        !string.Equals(previous.DisplayName, current.DisplayName, StringComparison.Ordinal)
        || !string.Equals(previous.ButtonMapTemplate, current.ButtonMapTemplate, StringComparison.OrdinalIgnoreCase);

    private sealed class DeviceWindow(DeviceProfile profile)
    {
        public DeviceProfile Profile { get; set; } = profile;
        public IRuntimeButtonMapView? View { get; set; }
        public EventHandler? HiddenByUser { get; set; }
        public bool Eligible { get; set; } = true;
        public bool ManualVisible { get; set; }
        public bool MappedVisible { get; set; }
        public bool MappedSuppressedUntilFalse { get; set; }
        public bool RenderedVisible { get; set; }
    }

    private sealed class ButtonMapViewFactory : IRuntimeButtonMapViewFactory
    {
        public IRuntimeButtonMapView Create(CompanionConfig config, string deviceId, string statePath) =>
            new ButtonMapView(new ButtonMapForm(config, deviceId, statePath));
    }

    private sealed class ButtonMapView : IRuntimeButtonMapView
    {
        private readonly ButtonMapForm _form;
        private bool _hidingByAdapter;

        public ButtonMapView(ButtonMapForm form)
        {
            _form = form;
            _form.VisibleChanged += OnVisibleChanged;
        }

        public event EventHandler? HiddenByUser;

        public void UpdateConfig(CompanionConfig config) => _form.UpdateConfig(config);

        public void UpdateTaskAlerts(
            IReadOnlyList<TaskAlertAssignment> assignments,
            TaskAlertLedOptions? ledOptions) => _form.UpdateTaskAlerts(assignments, ledOptions);

        public void ShowReference() => _form.ShowReference();

        public void HideReference()
        {
            _hidingByAdapter = true;
            try
            {
                _form.HideReference();
            }
            finally
            {
                _hidingByAdapter = false;
            }
        }

        public void Dispose()
        {
            _form.VisibleChanged -= OnVisibleChanged;
            _form.Dispose();
        }

        private void OnVisibleChanged(object? sender, EventArgs eventArgs)
        {
            if (!_form.Visible && !_hidingByAdapter)
            {
                HiddenByUser?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
