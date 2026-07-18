namespace Joydex.App;

internal sealed class DeviceChangeMonitor : NativeWindow, IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 1000 };

    public DeviceChangeMonitor()
    {
        CreateHandle(new CreateParams { Caption = "Joydex device monitor" });
        _debounce.Tick += OnDebounceTick;
    }

    public event EventHandler? DevicesChanged;

    public void Dispose()
    {
        _debounce.Stop();
        _debounce.Tick -= OnDebounceTick;
        _debounce.Dispose();
        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmDeviceChange
            && message.WParam.ToInt32() is DbtDevNodesChanged or DbtDeviceArrival)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        base.WndProc(ref message);
    }

    private void OnDebounceTick(object? sender, EventArgs eventArgs)
    {
        _debounce.Stop();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }
}
