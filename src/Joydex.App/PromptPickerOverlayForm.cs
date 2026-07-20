using System.Runtime.InteropServices;

namespace Joydex.App;

internal sealed class PromptPickerOverlayForm : Form
{
    private const int HotKeyId = 0x4A50;
    private const int WmHotKey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly Label _title = new();
    private readonly TableLayoutPanel _rows = new();
    private readonly System.Windows.Forms.Timer _foregroundTimer = new() { Interval = 250 };
    private readonly Func<bool> _codexStillForeground;
    private readonly Font _normalFont = new("Segoe UI", 11F, FontStyle.Regular);
    private readonly Font _selectedFont = new("Segoe UI Semibold", 11F, FontStyle.Bold);
    private bool _hotKeyRegistered;

    public PromptPickerOverlayForm(Func<bool> codexStillForeground)
    {
        _codexStillForeground = codexStillForeground;
        Text = "Joydex Prompt Picker";
        Font = new Font("Segoe UI", 11F);
        BackColor = Color.FromArgb(24, 27, 32);
        ForeColor = Color.White;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(720, 330);
        Padding = new Padding(14);

        _title.Dock = DockStyle.Top;
        _title.Height = 42;
        _title.Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold);
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _rows.Dock = DockStyle.Fill;
        _rows.Padding = new Padding(0, 8, 0, 0);
        _rows.ColumnCount = 1;
        _rows.RowCount = 5;
        for (var index = 0; index < 5; index++)
        {
            _rows.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
            _rows.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                Padding = new Padding(12, 5, 12, 5),
                TextAlign = ContentAlignment.MiddleLeft,
            }, 0, index);
        }

        Controls.Add(_rows);
        Controls.Add(_title);
        _foregroundTimer.Tick += (_, _) =>
        {
            if (Visible && !_codexStillForeground())
            {
                DismissRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler? DismissRequested;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= wsExToolWindow | wsExNoActivate;
            return parameters;
        }
    }

    public void Apply(PromptPickerSnapshot snapshot)
    {
        if (!snapshot.Visible)
        {
            HidePicker();
            return;
        }

        _title.Text = $"{snapshot.PickerName}   ·   Press Insert to use   ·   Esc cancels";
        var visibleIndices = VisibleIndices(snapshot.Prompts.Count, snapshot.SelectedIndex);
        for (var row = 0; row < 5; row++)
        {
            var label = (Label)_rows.Controls[row];
            if (row >= visibleIndices.Count)
            {
                label.Text = string.Empty;
                label.BackColor = BackColor;
                label.ForeColor = Color.White;
                continue;
            }

            var promptIndex = visibleIndices[row];
            var selected = promptIndex == snapshot.SelectedIndex;
            var prompt = snapshot.Prompts[promptIndex].Replace("\r\n", " ↵ ").Replace('\n', ' ');
            label.Text = $"{(selected ? "▶" : " ")}  {prompt}";
            label.BackColor = selected ? Color.FromArgb(36, 103, 180) : BackColor;
            label.ForeColor = Color.White;
            label.Font = selected ? _selectedFont : _normalFont;
        }

        PositionNearForegroundWindow();
        if (!Visible)
        {
            Show();
        }

        _foregroundTimer.Start();
        if (!_hotKeyRegistered)
        {
            _hotKeyRegistered = RegisterHotKey(Handle, HotKeyId, ModNoRepeat, (uint)Keys.Escape);
        }
    }

    public void HidePicker()
    {
        _foregroundTimer.Stop();
        if (_hotKeyRegistered)
        {
            UnregisterHotKey(Handle, HotKeyId);
            _hotKeyRegistered = false;
        }

        Hide();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotKey && message.WParam.ToInt32() == HotKeyId)
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HidePicker();
            _foregroundTimer.Dispose();
            _normalFont.Dispose();
            _selectedFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private static IReadOnlyList<int> VisibleIndices(int count, int selected)
    {
        if (count <= 5)
        {
            return Enumerable.Range(0, count).ToArray();
        }

        return Enumerable.Range(-2, 5).Select(offset => (selected + offset + count) % count).ToArray();
    }

    private void PositionNearForegroundWindow()
    {
        var window = GetForegroundWindow();
        Rectangle area;
        if (window != IntPtr.Zero && GetWindowRect(window, out var rectangle))
        {
            area = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        }
        else
        {
            area = Screen.FromPoint(Cursor.Position).WorkingArea;
        }

        Location = new Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            Math.Max(area.Top, area.Bottom - Height - 120));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
}
