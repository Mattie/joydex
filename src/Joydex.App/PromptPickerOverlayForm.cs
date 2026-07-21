using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Joydex.App;

internal sealed class PromptPickerOverlayForm : Form
{
    private const int HotKeyId = 0x4A50;
    private const int WmHotKey = 0x0312;
    private const int CsDropShadow = 0x00020000;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const uint ModNoRepeat = 0x4000;
    private readonly FlowLayoutPanel _header = new();
    private readonly Label _pickerName = new();
    private readonly Label _hint = new();
    private readonly TableLayoutPanel _rows = new();
    private readonly PromptPickerRow[] _rowControls = new PromptPickerRow[5];
    private readonly TableLayoutPanel _footer = new();
    private readonly Label _footerHint = new();
    private readonly Label _position = new();
    private readonly System.Windows.Forms.Timer _foregroundTimer = new() { Interval = 250 };
    private readonly Func<bool> _codexStillForeground;
    private readonly Font _headerNameFont = new(
        JoydexTheme.UiSemiboldFont.FontFamily,
        14F,
        FontStyle.Bold);
    private readonly Font _headerHintFont = new(
        JoydexTheme.UiFont.FontFamily,
        12F,
        FontStyle.Regular);
    private readonly Font _normalFont = new(
        JoydexTheme.UiFont.FontFamily,
        13.5F,
        FontStyle.Regular);
    private readonly Font _selectedFont = new(
        JoydexTheme.UiSemiboldFont.FontFamily,
        13.5F,
        FontStyle.Bold);
    private readonly Font _glyphFont = new(
        JoydexTheme.UiFont.FontFamily,
        12F,
        FontStyle.Regular);
    private readonly Font _footerFont = new(
        JoydexTheme.UiFont.FontFamily,
        11.5F,
        FontStyle.Regular);
    private bool _hotKeyRegistered;

    public PromptPickerOverlayForm(Func<bool> codexStillForeground)
    {
        _codexStillForeground = codexStillForeground;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Joydex Prompt Picker";
        Font = _normalFont;
        BackColor = OverlayPalette.WindowBg;
        ForeColor = OverlayPalette.Text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(728, 390);
        Padding = new Padding(8);

        _header.Dock = DockStyle.Top;
        _header.Height = 55;
        _header.Padding = new Padding(16, 13, 0, 8);
        _header.FlowDirection = FlowDirection.LeftToRight;
        _header.WrapContents = false;
        _header.BackColor = Color.Transparent;

        _pickerName.AutoSize = true;
        _pickerName.Font = _headerNameFont;
        _pickerName.ForeColor = OverlayPalette.Text;
        _pickerName.BackColor = Color.Transparent;
        _pickerName.Margin = System.Windows.Forms.Padding.Empty;

        _hint.AutoSize = true;
        _hint.Text = "Insert to use · Esc cancels";
        _hint.Font = _headerHintFont;
        _hint.ForeColor = OverlayPalette.TextFaint;
        _hint.BackColor = Color.Transparent;
        _hint.Margin = new Padding(16, 3, 0, 0);

        _header.Controls.Add(_pickerName);
        _header.Controls.Add(_hint);

        _rows.Dock = DockStyle.Fill;
        _rows.Padding = new Padding(8, 5, 8, 0);
        _rows.BackColor = Color.Transparent;
        _rows.ColumnCount = 1;
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _rows.RowCount = 5;
        _rows.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
        for (var index = 0; index < 5; index++)
        {
            _rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 49F));
            var row = new PromptPickerRow(_normalFont, _selectedFont, _glyphFont)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 3),
            };
            _rowControls[index] = row;
            _rows.Controls.Add(row, 0, index);
        }

        _footer.Dock = DockStyle.Bottom;
        _footer.Height = 42;
        _footer.Padding = new Padding(16, 1, 16, 0);
        _footer.BackColor = OverlayPalette.FooterBg;
        _footer.ColumnCount = 2;
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.RowCount = 1;
        _footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _footer.Paint += (_, eventArgs) =>
        {
            using var borderPen = new Pen(OverlayPalette.Border);
            eventArgs.Graphics.DrawLine(borderPen, 0, 0, _footer.ClientSize.Width, 0);
        };

        _footerHint.Dock = DockStyle.Fill;
        _footerHint.Text = "Roll to select Press to confirm";
        _footerHint.Font = _footerFont;
        _footerHint.ForeColor = OverlayPalette.TextSub;
        _footerHint.TextAlign = ContentAlignment.MiddleLeft;
        _footerHint.Margin = System.Windows.Forms.Padding.Empty;

        _position.AutoSize = true;
        _position.Dock = DockStyle.Fill;
        _position.Font = _footerFont;
        _position.ForeColor = OverlayPalette.TextFaint;
        _position.TextAlign = ContentAlignment.MiddleRight;
        _position.Margin = System.Windows.Forms.Padding.Empty;

        _footer.Controls.Add(_footerHint, 0, 0);
        _footer.Controls.Add(_position, 1, 0);

        Controls.Add(_rows);
        Controls.Add(_footer);
        Controls.Add(_header);
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
            parameters.ClassStyle |= CsDropShadow;
            parameters.ExStyle |= wsExToolWindow | wsExNoActivate;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);

        var cornerPreference = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));

        var borderColor = OverlayPalette.DwmBorderColor;
        DwmSetWindowAttribute(Handle, DwmBorderColor, ref borderColor, sizeof(int));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        PositionNearForegroundWindow();
        Invalidate(true);
    }

    public void Apply(PromptPickerSnapshot snapshot)
    {
        if (!snapshot.Visible)
        {
            HidePicker();
            return;
        }

        _pickerName.Text = snapshot.PickerName;
        _position.Text = $"{snapshot.SelectedIndex + 1} / {snapshot.Prompts.Count}";
        var visibleIndices = VisibleIndices(snapshot.Prompts.Count, snapshot.SelectedIndex);
        for (var row = 0; row < 5; row++)
        {
            if (row >= visibleIndices.Count)
            {
                _rowControls[row].SetContent(string.Empty, selected: false, isExit: false, submits: false);
                continue;
            }

            var promptIndex = visibleIndices[row];
            var selected = promptIndex == snapshot.SelectedIndex;
            var prompt = snapshot.Prompts[promptIndex].Replace("\r\n", " ↵ ").Replace('\n', ' ');
            var isExit = string.Equals(
                snapshot.Prompts[promptIndex],
                PromptPickerCoordinator.ExitOptionLabel,
                StringComparison.Ordinal);
            var submits = promptIndex < snapshot.SubmitAfterInsert.Count
                && snapshot.SubmitAfterInsert[promptIndex];
            _rowControls[row].SetContent(prompt, selected, isExit, submits);
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
            _headerNameFont.Dispose();
            _headerHintFont.Dispose();
            _normalFont.Dispose();
            _selectedFont.Dispose();
            _glyphFont.Dispose();
            _footerFont.Dispose();
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

    private sealed class PromptPickerRow : Control
    {
        private const int CornerRadius = 7;
        private readonly Font _normalFont;
        private readonly Font _selectedFont;
        private readonly Font _glyphFont;
        private string _text = string.Empty;
        private bool _selected;
        private bool _isExit;
        private bool _submits;

        public PromptPickerRow(Font normalFont, Font selectedFont, Font glyphFont)
        {
            _normalFont = normalFont;
            _selectedFont = selectedFont;
            _glyphFont = glyphFont;
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
        }

        public void SetContent(string text, bool selected, bool isExit, bool submits)
        {
            if (string.Equals(_text, text, StringComparison.Ordinal)
                && _selected == selected
                && _isExit == isExit
                && _submits == submits)
            {
                return;
            }

            _text = text;
            _selected = selected;
            _isExit = isExit;
            _submits = submits;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            if (string.IsNullOrEmpty(_text))
            {
                return;
            }

            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (_selected)
            {
                var selectionBounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
                using var selectionPath = ThemeDrawing.RoundedRectangle(
                    selectionBounds,
                    JoydexTheme.ScaleLogical(CornerRadius, DeviceDpi));
                using var selectionBrush = new SolidBrush(OverlayPalette.Selection);
                eventArgs.Graphics.FillPath(selectionBrush, selectionPath);
            }

            const TextFormatFlags textFlags = TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.SingleLine;
            const string glyph = "▶";
            var glyphSize = TextRenderer.MeasureText(glyph, _glyphFont, Size.Empty, TextFormatFlags.NoPadding);
            var horizontalInset = JoydexTheme.ScaleLogical(16, DeviceDpi);
            var contentGap = JoydexTheme.ScaleLogical(13, DeviceDpi);
            var glyphBounds = new Rectangle(horizontalInset, 0, glyphSize.Width, Height);
            if (_selected)
            {
                TextRenderer.DrawText(
                    eventArgs.Graphics,
                    glyph,
                    _glyphFont,
                    glyphBounds,
                    OverlayPalette.Accent,
                    textFlags);
            }

            const string submitMarker = "[+ Submit]";
            var textLeft = glyphBounds.Right + contentGap;
            var textRight = Width - horizontalInset;
            if (_submits)
            {
                var markerSize = TextRenderer.MeasureText(
                    submitMarker,
                    _normalFont,
                    Size.Empty,
                    TextFormatFlags.NoPadding);
                var markerBounds = new Rectangle(
                    Math.Max(textLeft, textRight - markerSize.Width),
                    0,
                    markerSize.Width,
                    Height);
                TextRenderer.DrawText(
                    eventArgs.Graphics,
                    submitMarker,
                    _normalFont,
                    markerBounds,
                    _selected ? OverlayPalette.AccentText : OverlayPalette.TagText,
                    textFlags);
                textRight = markerBounds.Left - contentGap;
            }

            var textBounds = new Rectangle(textLeft, 0, Math.Max(0, textRight - textLeft), Height);
            var textColor = _selected
                ? OverlayPalette.Text
                : _isExit
                    ? OverlayPalette.TagText
                    : OverlayPalette.Text;
            TextRenderer.DrawText(
                eventArgs.Graphics,
                _text,
                _selected ? _selectedFont : _normalFont,
                textBounds,
                textColor,
                textFlags);
        }

    }

    /// <summary>
    /// Keeps the no-activation overlay consistently dark while matching the shared Joydex palette.
    /// </summary>
    private static class OverlayPalette
    {
        public const int DwmBorderColor = 0x00453934;

        public static readonly Color WindowBg = Color.FromArgb(29, 32, 38);
        public static readonly Color FooterBg = Color.FromArgb(24, 27, 33);
        public static readonly Color Surface = Color.FromArgb(35, 38, 45);
        public static readonly Color Border = Color.FromArgb(52, 57, 69);
        public static readonly Color Text = Color.FromArgb(231, 233, 238);
        public static readonly Color TextSub = Color.FromArgb(154, 162, 174);
        public static readonly Color TextFaint = Color.FromArgb(111, 119, 132);
        public static readonly Color Accent = Color.FromArgb(139, 161, 242);
        public static readonly Color AccentText = Color.FromArgb(165, 182, 245);
        public static readonly Color TagText = Color.FromArgb(170, 177, 188);
        public static readonly Color Selection = JoydexTheme.Blend(Accent, Surface, 0.18F);
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
