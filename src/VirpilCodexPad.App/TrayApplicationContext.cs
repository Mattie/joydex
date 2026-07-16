using System.Diagnostics;
using VirpilCodexPad.Core.Config;
using VirpilCodexPad.Core.Mapping;
using VirpilCodexPad.Windows.Actions;
using VirpilCodexPad.Windows.Input;
using VirpilCodexPad.Windows.Interop;
using VirpilCodexPad.Windows.Runtime;

namespace VirpilCodexPad.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string _configPath;
    private readonly string _windowStatePath;
    private readonly string _buttonMapStatePath;
    private readonly FileLog _log;
    private readonly CooperativeWindow _cooperativeWindow;
    private readonly Icon _appIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _modeItem;
    private readonly ToolStripMenuItem _testControlsItem;
    private readonly ToolStripMenuItem _buttonMapItem;
    private readonly ToolStripMenuItem _configureItem;
    private readonly SynchronizationContext _uiContext;
    private readonly Queue<string> _recentActivity = new();
    private CompanionWorker? _worker;
    private CompanionConfig? _activeConfig;
    private DryRunActivityForm? _activityForm;
    private ButtonMapForm? _buttonMapForm;
    private bool _configuring;

    public TrayApplicationContext(string configPath)
    {
        _configPath = configPath;
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? throw new InvalidOperationException("The configuration path has no parent directory.");
        _windowStatePath = Path.Combine(dataDirectory, "configuration-window.json");
        _buttonMapStatePath = Path.Combine(dataDirectory, "button-map-window.json");
        _log = new FileLog(Path.Combine(dataDirectory, "virpil-codex-pad.log"));
        var shortcutsMigrated = TryMigrateLegacyCodexShortcuts();
        _cooperativeWindow = new CooperativeWindow("Virpil Codex Pad");
        _appIcon = AppIconFactory.Create();
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _modeItem = new ToolStripMenuItem("Dry run", image: null, OnToggleDryRun)
        {
            CheckOnClick = false,
            Enabled = false,
        };
        _testControlsItem = new ToolStripMenuItem("Test controls…", image: null, OnTestControls);
        _configureItem = new ToolStripMenuItem("Configure…", image: null, OnConfigure);
        _buttonMapItem = new ToolStripMenuItem("Button map...", image: null, OnToggleButtonMap)
        {
            Enabled = false,
        };
        var reloadItem = new ToolStripMenuItem("Reload config", image: null, OnReloadConfig);
        var openConfigItem = new ToolStripMenuItem("Open config JSON (advanced)", image: null, (_, _) => OpenPath(_configPath));
        var openLogItem = new ToolStripMenuItem("Open log", image: null, (_, _) => OpenPath(_log.Path));
        var exitItem = new ToolStripMenuItem("Exit", image: null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = new ContextMenuStrip
            {
                Items =
                {
                    _statusItem,
                    _modeItem,
                    new ToolStripSeparator(),
                    _testControlsItem,
                    _buttonMapItem,
                    _configureItem,
                    reloadItem,
                    openConfigItem,
                    openLogItem,
                    new ToolStripSeparator(),
                    exitItem,
                },
            },
            Icon = _appIcon,
            Text = "Virpil Codex Pad",
            Visible = true,
        };
        _notifyIcon.DoubleClick += OnConfigure;
        if (shortcutsMigrated)
        {
            _notifyIcon.ShowBalloonTip(
                6000,
                "Codex shortcuts repaired",
                "Restart Codex once to activate the updated throttle shortcuts.",
                ToolTipIcon.Info);
        }

        var firstRun = !File.Exists(_configPath);
        StartWorker(showFirstRunNotice: firstRun);
        if (firstRun)
        {
            _uiContext.Post(_ => OnConfigure(this, EventArgs.Empty), null);
        }
        else if (_activeConfig?.Safety.DryRun == true)
        {
            _uiContext.Post(_ => OnTestControls(this, EventArgs.Empty), null);
        }
    }

    private bool TryMigrateLegacyCodexShortcuts()
    {
        try
        {
            var migrated = CodexShortcutInstaller.MigrateLegacyExtendedFunctionBindings();
            if (migrated)
            {
                _log.Write("Migrated Codex shortcuts from F13-F24 to Ctrl+Alt+Shift+F1-F12.");
            }

            return migrated;
        }
        catch (Exception exception)
        {
            _log.Write($"Could not migrate legacy Codex shortcuts: {exception.Message}");
            return false;
        }
    }

    protected override void ExitThreadCore()
    {
        _activityForm?.Close();
        _activityForm = null;
        if (_buttonMapForm is not null)
        {
            _buttonMapForm.SaveWindowState();
            _buttonMapForm.Dispose();
            _buttonMapForm = null;
        }

        if (_worker is not null)
        {
            _worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _worker = null;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        _cooperativeWindow.Dispose();
        base.ExitThreadCore();
    }

    private async void OnReloadConfig(object? sender, EventArgs eventArgs)
    {
        try
        {
            CloseActivityForm();
            HideButtonMap();
            _recentActivity.Clear();
            if (_worker is not null)
            {
                await _worker.DisposeAsync();
                _worker = null;
            }

            StartWorker(showFirstRunNotice: false);
            if (_activeConfig?.Safety.DryRun == true)
            {
                OnTestControls(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            HandleConfigurationError(exception);
        }
    }

    private async void OnToggleDryRun(object? sender, EventArgs eventArgs)
    {
        if (_configuring || _activeConfig is null)
        {
            return;
        }

        _modeItem.Enabled = false;
        try
        {
            var current = _activeConfig;
            var enableDryRun = !current.Safety.DryRun;
            var updated = new CompanionConfig
            {
                Device = current.Device,
                Polling = current.Polling,
                Safety = new SafetyOptions
                {
                    DryRun = enableDryRun,
                    RequireCodexForeground = current.Safety.RequireCodexForeground,
                    CodexProcessNames = current.Safety.CodexProcessNames,
                    SimulatorProcessNames = current.Safety.SimulatorProcessNames,
                },
                BankSelectors = current.BankSelectors,
                Bindings = current.Bindings,
            };

            ConfigStore.Save(_configPath, updated);
            CloseActivityForm();
            HideButtonMap();
            _recentActivity.Clear();
            if (_worker is not null)
            {
                await _worker.DisposeAsync();
                _worker = null;
            }

            StartWorker(showFirstRunNotice: false);
            if (enableDryRun && _activeConfig?.Safety.DryRun == true)
            {
                OnTestControls(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            HandleConfigurationError(exception);
        }
        finally
        {
            _modeItem.Enabled = _activeConfig is not null;
        }
    }

    private async void OnConfigure(object? sender, EventArgs eventArgs)
    {
        if (_configuring)
        {
            return;
        }

        _configuring = true;
        _configureItem.Enabled = false;
        _modeItem.Enabled = false;
        try
        {
            CloseActivityForm();
            HideButtonMap();
            _recentActivity.Clear();
            if (_worker is not null)
            {
                await _worker.DisposeAsync();
                _worker = null;
            }

            using var form = new ConfigurationForm(_configPath, _windowStatePath, _cooperativeWindow.Handle);
            var result = form.ShowDialog();
            StartWorker(showFirstRunNotice: false);
            if (result == DialogResult.OK)
            {
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "Virpil Codex Pad configuration saved",
                    "The throttle mapping has been reloaded.",
                    ToolTipIcon.Info);

                if (_activeConfig?.Safety.DryRun == true)
                {
                    OnTestControls(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception exception)
        {
            HandleConfigurationError(exception);
        }
        finally
        {
            if (_worker is null)
            {
                StartWorker(showFirstRunNotice: false);
            }

            _configuring = false;
            _configureItem.Enabled = true;
            _modeItem.Enabled = _activeConfig is not null;
        }
    }

    private void StartWorker(bool showFirstRunNotice)
    {
        try
        {
            var config = ConfigStore.LoadOrCreate(_configPath);
            _activeConfig = config;
            _modeItem.Text = "Dry run";
            _modeItem.Checked = config.Safety.DryRun;
            _modeItem.Enabled = true;
            _modeItem.ForeColor = config.Safety.DryRun ? Color.DarkGreen : Color.DarkRed;
            _testControlsItem.Enabled = config.Safety.DryRun;
            _buttonMapItem.Enabled = true;
            _buttonMapForm?.UpdateConfig(config);

            var source = new DirectInputJoystickSource(_cooperativeWindow.Handle);
            var executor = new CodexActionExecutor(
                config.Safety,
                WriteActivity,
                internalAction: OnInternalAction);
            _worker = new CompanionWorker(config, source, executor, WriteActivity);
            _worker.StatusChanged += OnStatusChanged;
            _worker.Start();

            if (showFirstRunNotice)
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    "Virpil Codex Pad is in dry-run mode",
                    "Use Test controls to see each mapped throttle press.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception exception)
        {
            HandleConfigurationError(exception);
        }
    }

    private void OnStatusChanged(object? sender, string status)
    {
        _uiContext.Post(_ =>
        {
            _statusItem.Text = status;
            _notifyIcon.Text = TruncateTooltip($"Virpil Codex Pad — {status}");
            _activityForm?.SetConnectionStatus(status);
        }, null);
    }

    private void OnTestControls(object? sender, EventArgs eventArgs)
    {
        if (_activeConfig?.Safety.DryRun != true)
        {
            MessageBox.Show(
                "Enable Dry run in Configure before testing controls.",
                "Test Virpil Codex Pad",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_activityForm is { IsDisposed: false })
        {
            _activityForm.Show();
            _activityForm.BringToFront();
            _activityForm.Activate();
            return;
        }

        _activityForm = new DryRunActivityForm(_activeConfig);
        _activityForm.FormClosed += (_, _) => _activityForm = null;
        foreach (var message in _recentActivity)
        {
            _activityForm.Append(message);
        }

        _activityForm.SetConnectionStatus(_statusItem.Text ?? "Starting...");
        _activityForm.Show();
    }

    private void OnToggleButtonMap(object? sender, EventArgs eventArgs)
    {
        if (_buttonMapForm is { Visible: true })
        {
            HideButtonMap();
        }
        else
        {
            ShowButtonMap();
        }
    }

    private void OnInternalAction(ActionRequest request)
    {
        _uiContext.Post(_ =>
        {
            if (string.Equals(request.Trigger, "release", StringComparison.OrdinalIgnoreCase))
            {
                HideButtonMap();
            }
            else
            {
                ShowButtonMap();
            }
        }, null);
    }

    private void ShowButtonMap()
    {
        if (_activeConfig is null)
        {
            return;
        }

        try
        {
            if (_buttonMapForm is null || _buttonMapForm.IsDisposed)
            {
                _buttonMapForm = new ButtonMapForm(_activeConfig, _buttonMapStatePath);
                _buttonMapForm.VisibleChanged += (_, _) =>
                    _buttonMapItem.Checked = _buttonMapForm is { Visible: true };
            }
            else
            {
                _buttonMapForm.UpdateConfig(_activeConfig);
            }

            _buttonMapForm.ShowReference();
            _buttonMapItem.Checked = true;
        }
        catch (Exception exception)
        {
            _log.Write($"Could not show the button map: {exception.Message}");
            _notifyIcon.ShowBalloonTip(
                5000,
                "Virpil Codex Pad button map",
                exception.Message,
                ToolTipIcon.Error);
        }
    }

    private void HideButtonMap()
    {
        _buttonMapForm?.HideReference();
        _buttonMapItem.Checked = false;
    }

    private void WriteActivity(string message)
    {
        _log.Write(message);
        if (!IsActionActivity(message))
        {
            return;
        }

        _uiContext.Post(_ =>
        {
            _recentActivity.Enqueue(message);
            while (_recentActivity.Count > 200)
            {
                _recentActivity.Dequeue();
            }

            _activityForm?.Append(message);
        }, null);
    }

    private void CloseActivityForm()
    {
        _activityForm?.Close();
        _activityForm = null;
    }

    private void HandleConfigurationError(Exception exception)
    {
        _log.Write($"Configuration error: {exception.Message}");
        _activeConfig = null;
        _statusItem.Text = "Configuration error";
        _modeItem.Text = "Dry run unavailable";
        _modeItem.Checked = false;
        _modeItem.Enabled = false;
        _modeItem.ForeColor = Color.DarkRed;
        _testControlsItem.Enabled = false;
        _buttonMapItem.Enabled = false;
        _notifyIcon.ShowBalloonTip(
            7000,
            "Virpil Codex Pad configuration error",
            exception.Message,
            ToolTipIcon.Error);
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private static bool IsActionActivity(string message) =>
        message.StartsWith("INPUT ", StringComparison.Ordinal)
        || message.StartsWith("DRY RUN ", StringComparison.Ordinal)
        || message.StartsWith("BLOCKED ", StringComparison.Ordinal)
        || message.StartsWith("EXECUTED ", StringComparison.Ordinal);
}
