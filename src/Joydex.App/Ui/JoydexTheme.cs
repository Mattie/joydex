using System.Drawing.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Joydex.App;

internal enum ThemeTone
{
    Default,
    Subtle,
    Faint,
    Accent,
    Success,
}

/// <summary>
/// Centralizes the colors, fonts, and sizing used by Joydex windows.
/// </summary>
internal static class JoydexTheme
{
    public const int StandardControlHeight = 36;
    public const int CompactControlHeight = 32;
    public const int GridRowHeight = 36;
    public const int GridHeaderHeight = 36;

    public static int ScaleLogical(int value, int dpi) => (value * dpi + 48) / 96;

    private static readonly bool SystemDarkMode = ReadSystemDarkMode();
    private static bool? _darkModeOverride;

    public static bool Dark { get; private set; } = SystemDarkMode;

    public static bool HighContrast => SystemInformation.HighContrast;

    public static Color WindowBg => HighContrast
        ? SystemColors.Control
        : Dark ? FromHex("#1D2026") : FromHex("#F4F5F7");

    public static Color Surface => HighContrast
        ? SystemColors.Window
        : Dark ? FromHex("#23262D") : Color.White;

    public static Color InputBg => HighContrast
        ? SystemColors.Window
        : Dark ? FromHex("#20232A") : Color.White;

    public static Color Border => HighContrast
        ? SystemColors.WindowFrame
        : Dark ? FromHex("#343945") : FromHex("#D8DCE2");

    public static Color BorderSoft => HighContrast
        ? SystemColors.ControlDark
        : Dark ? FromHex("#2C3038") : FromHex("#F1F3F6");

    public static Color Text => HighContrast
        ? SystemColors.WindowText
        : Dark ? FromHex("#E7E9EE") : FromHex("#1B1E24");

    public static Color TextSub => HighContrast
        ? SystemColors.WindowText
        : Dark ? FromHex("#9AA2AE") : FromHex("#5B6470");

    public static Color TextFaint => HighContrast
        ? SystemColors.GrayText
        : Dark ? FromHex("#6F7784") : FromHex("#8A919C");

    public static Color Accent => HighContrast
        ? SystemColors.Highlight
        : Dark ? FromHex("#8BA1F2") : FromHex("#3D63DD");

    public static Color AccentText => HighContrast
        ? SystemColors.HotTrack
        : Dark ? FromHex("#A5B6F5") : FromHex("#2F4FC4");

    // DataGridView backgrounds must be opaque, so the accent is blended into the surface.
    public static Color AccentTint => Blend(Accent, Surface, Dark ? 0.18F : 0.10F);

    public static Color HoverBg => HighContrast
        ? SystemColors.ControlLight
        : Dark ? FromHex("#2B3039") : FromHex("#ECEFF4");

    public static Color GroupBg => HighContrast
        ? SystemColors.Control
        : Dark ? FromHex("#272B33") : FromHex("#F7F8FA");

    public static Color TagBg => HighContrast
        ? SystemColors.Control
        : Dark ? FromHex("#2C3038") : FromHex("#EEF0F4");

    public static Color TagText => HighContrast
        ? SystemColors.ControlText
        : Dark ? FromHex("#AAB1BC") : FromHex("#5A6270");

    public static Color TagWarnBg => HighContrast
        ? SystemColors.Control
        : Dark ? FromHex("#3A2E1F") : FromHex("#FBEEDD");

    public static Color TagWarnText => HighContrast
        ? SystemColors.ControlText
        : Dark ? FromHex("#E0A05C") : FromHex("#A05F17");

    public static Color Success => HighContrast ? SystemColors.Highlight : FromHex("#37B26C");

    public static Color PrimaryText => Dark ? FromHex("#171A20") : Color.White;

    public static Color DisabledBg => Blend(TextFaint, Surface, 0.10F);

    public static Color DisabledText => Blend(TextFaint, Surface, 0.72F);

    public static Font UiFont { get; } = CreateFont(
        ["Segoe UI Variable Text", "Segoe UI"],
        9.75F,
        FontStyle.Regular);

    public static Font UiSemiboldFont { get; } = CreateFont(
        ["Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI"],
        9.75F,
        FontStyle.Bold);

    public static Font SectionFont { get; } = CreateFont(
        ["Segoe UI Semibold", "Segoe UI"],
        8.25F,
        FontStyle.Bold);

    public static Font MonoFont { get; } = CreateFont(
        ["Cascadia Mono", "Consolas"],
        9F,
        FontStyle.Regular);

    private static readonly ConditionalWeakTable<Control, RoleFontCache> RoleFonts = new();

    /// <summary>
    /// Returns a role font whose size follows the control font WinForms has already
    /// adjusted for the control's current monitor.
    /// </summary>
    public static Font FontFor(Control control, Font roleFont)
        => FontFor(control, roleFont, UiFont.SizeInPoints);

    public static Font FontFor(Control control, Font roleFont, float logicalBaseFontSize)
    {
        if (!RoleFonts.TryGetValue(control, out var cache))
        {
            cache = new RoleFontCache();
            RoleFonts.Add(control, cache);
            control.Disposed += OnRoleFontControlDisposed;
        }

        return cache.Get(roleFont, logicalBaseFontSize, control.Font, control.DeviceDpi);
    }

    public static bool RefreshSystemPreference()
    {
        var next = _darkModeOverride ?? ReadSystemDarkMode();
        if (Dark == next)
        {
            return false;
        }

        Dark = next;
        return true;
    }

    /// <summary>
    /// Temporarily pins the palette for deterministic rendering and tests.
    /// </summary>
    public static IDisposable OverrideDarkMode(bool dark)
    {
        var previousOverride = _darkModeOverride;
        var previousDark = Dark;
        _darkModeOverride = dark;
        Dark = dark;
        return new ThemeOverride(previousOverride, previousDark);
    }

    public static Color Blend(Color foreground, Color background, float amount)
    {
        amount = Math.Clamp(amount, 0F, 1F);
        return Color.FromArgb(
            255,
            (int)Math.Round(background.R + ((foreground.R - background.R) * amount)),
            (int)Math.Round(background.G + ((foreground.G - background.G) * amount)),
            (int)Math.Round(background.B + ((foreground.B - background.B) * amount)));
    }

    public static Color Shift(Color color, float amount)
    {
        var target = amount < 0 ? Color.Black : Color.White;
        return Blend(target, color, Math.Abs(amount));
    }

    private static bool ReadSystemDarkMode()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Font CreateFont(IReadOnlyList<string> candidates, float size, FontStyle style)
    {
        try
        {
            using var installed = new InstalledFontCollection();
            var names = installed.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (names.Contains(candidate))
                {
                    return new Font(candidate, size, style, GraphicsUnit.Point);
                }
            }
        }
        catch
        {
            // Font discovery can fail in restricted sessions. Segoe UI ships with supported Windows versions.
        }

        return new Font("Segoe UI", size, style, GraphicsUnit.Point);
    }

    private static void OnRoleFontControlDisposed(object? sender, EventArgs eventArgs)
    {
        if (sender is not Control control || !RoleFonts.TryGetValue(control, out var cache))
        {
            return;
        }

        control.Disposed -= OnRoleFontControlDisposed;
        RoleFonts.Remove(control);
        cache.Dispose();
    }

    private static Color FromHex(string value) => ColorTranslator.FromHtml(value);

    private sealed class ThemeOverride(bool? previousOverride, bool previousDark) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _darkModeOverride = previousOverride;
            Dark = previousOverride ?? previousDark;
            _disposed = true;
        }
    }

    private sealed class RoleFontCache : IDisposable
    {
        private readonly Dictionary<(Font Role, float LogicalBaseSize), Font> _fonts = [];
        private string? _sourceFamily;
        private float _sourceSize;
        private FontStyle _sourceStyle;
        private int _sourceDpi;

        public Font Get(Font roleFont, float logicalBaseFontSize, Font sourceFont, int sourceDpi)
        {
            if (!Matches(sourceFont, sourceDpi))
            {
                DisposeFonts();
                _sourceFamily = sourceFont.FontFamily.Name;
                _sourceSize = sourceFont.SizeInPoints;
                _sourceStyle = sourceFont.Style;
                _sourceDpi = sourceDpi;
            }

            var key = (roleFont, logicalBaseFontSize);
            if (_fonts.TryGetValue(key, out var font))
            {
                return font;
            }

            var roleScale = roleFont.SizeInPoints / logicalBaseFontSize;
            font = new Font(
                roleFont.FontFamily,
                Math.Max(1F, sourceFont.SizeInPoints * roleScale),
                roleFont.Style,
                GraphicsUnit.Point);
            _fonts.Add(key, font);
            return font;
        }

        public void Dispose() => DisposeFonts();

        private bool Matches(Font sourceFont, int sourceDpi) =>
            string.Equals(_sourceFamily, sourceFont.FontFamily.Name, StringComparison.Ordinal)
            && Math.Abs(_sourceSize - sourceFont.SizeInPoints) < 0.01F
            && _sourceStyle == sourceFont.Style
            && _sourceDpi == sourceDpi;

        private void DisposeFonts()
        {
            foreach (var font in _fonts.Values)
            {
                font.Dispose();
            }

            _fonts.Clear();
        }
    }
}

internal static class DpiUtilities
{
    public const int LogicalDpi = 96;

    public static int SystemDpi
    {
        get
        {
            try
            {
                return Math.Max(LogicalDpi, checked((int)GetDpiForSystem()));
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or OverflowException)
            {
                return LogicalDpi;
            }
        }
    }

    public static int ScaleBetween(int value, int sourceDpi, int targetDpi)
    {
        sourceDpi = sourceDpi > 0 ? sourceDpi : LogicalDpi;
        targetDpi = targetDpi > 0 ? targetDpi : LogicalDpi;
        return Math.Max(1, (int)Math.Round(value * (double)targetDpi / sourceDpi));
    }

    public static Size ScaleBetween(Size value, int sourceDpi, int targetDpi) => new(
        ScaleBetween(value.Width, sourceDpi, targetDpi),
        ScaleBetween(value.Height, sourceDpi, targetDpi));

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}

/// <summary>
/// Applies the current Joydex palette to stock controls and native title bars.
/// </summary>
internal static class ThemeService
{
    private const int DwmUseImmersiveDarkMode = 20;
    private static readonly ConditionalWeakTable<ComboBox, object> StyledComboBoxes = new();

    public static void Apply(Control root)
    {
        ApplyControl(root, JoydexTheme.WindowBg);
        if (root is Form form && form.IsHandleCreated)
        {
            ApplyTitleBar(form);
        }

        root.Invalidate(true);
    }

    public static void ApplyGrid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.GridColor = JoydexTheme.Border;
        grid.BackgroundColor = JoydexTheme.Surface;
        grid.RowHeadersVisible = false;
        grid.AllowUserToResizeRows = false;
        var rowHeight = JoydexTheme.ScaleLogical(JoydexTheme.GridRowHeight, grid.DeviceDpi);
        var headerHeight = JoydexTheme.ScaleLogical(JoydexTheme.GridHeaderHeight, grid.DeviceDpi);
        var horizontalPadding = JoydexTheme.ScaleLogical(6, grid.DeviceDpi);
        var topPadding = JoydexTheme.ScaleLogical(2, grid.DeviceDpi);
        var bottomPadding = JoydexTheme.ScaleLogical(3, grid.DeviceDpi);

        grid.RowTemplate.Height = rowHeight;
        grid.RowTemplate.DividerHeight = 0;
        foreach (DataGridViewRow row in grid.Rows)
        {
            row.Height = rowHeight;
            row.DividerHeight = 0;
        }

        grid.ColumnHeadersHeight = headerHeight;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        var sectionFont = JoydexTheme.FontFor(grid, JoydexTheme.SectionFont);
        var monoFont = JoydexTheme.FontFor(grid, JoydexTheme.MonoFont);
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            BackColor = JoydexTheme.Surface,
            ForeColor = JoydexTheme.TextFaint,
            Font = sectionFont,
            Padding = new Padding(6, 0, 6, 0),
            SelectionBackColor = JoydexTheme.Surface,
            SelectionForeColor = JoydexTheme.TextFaint,
        };
        grid.DefaultCellStyle.BackColor = JoydexTheme.Surface;
        grid.DefaultCellStyle.ForeColor = JoydexTheme.Text;
        grid.DefaultCellStyle.Font = grid.Font;
        grid.DefaultCellStyle.Padding = new Padding(
            horizontalPadding,
            topPadding,
            horizontalPadding,
            bottomPadding);
        grid.DefaultCellStyle.SelectionBackColor = JoydexTheme.AccentTint;
        grid.DefaultCellStyle.SelectionForeColor = JoydexTheme.Text;
        grid.AlternatingRowsDefaultCellStyle.BackColor = JoydexTheme.Surface;
        grid.RowsDefaultCellStyle.BackColor = JoydexTheme.Surface;

        foreach (DataGridViewColumn column in grid.Columns)
        {
            column.DividerWidth = 0;
            column.HeaderText = column.HeaderText.ToUpperInvariant();
            if (column is DataGridViewComboBoxColumn comboColumn)
            {
                comboColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing;
                comboColumn.DisplayStyleForCurrentCellOnly = false;
                comboColumn.FlatStyle = FlatStyle.Flat;
            }

            if (column.Name.Contains("Button", StringComparison.OrdinalIgnoreCase)
                || column.Name.Contains("Action", StringComparison.OrdinalIgnoreCase)
                || column.Name.Contains("Notches", StringComparison.OrdinalIgnoreCase)
                || column.Name.Contains("Slot", StringComparison.OrdinalIgnoreCase)
                || column.Name.Contains("Time", StringComparison.OrdinalIgnoreCase)
                || column.Name.Contains("Received", StringComparison.OrdinalIgnoreCase))
            {
                column.DefaultCellStyle.Font = monoFont;
            }

            if (column.Name.Contains("Action", StringComparison.OrdinalIgnoreCase))
            {
                column.DefaultCellStyle.ForeColor = JoydexTheme.AccentText;
                column.DefaultCellStyle.SelectionForeColor = JoydexTheme.AccentText;
            }
        }

        typeof(DataGridView)
            .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(grid, true);
    }

    public static void ApplyTitleBar(Form form)
    {
        if (!form.IsHandleCreated)
        {
            return;
        }

        var enabled = JoydexTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(
            form.Handle,
            DwmUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));
    }

    private static void ApplyControl(Control control, Color inheritedBackground)
    {
        // These controls carry authored visuals whose colors are part of their content or behavior.
        if (control is ButtonMapCanvas or PromptPickerOverlayForm)
        {
            return;
        }

        var childBackground = inheritedBackground;
        switch (control)
        {
            case Form:
                control.BackColor = JoydexTheme.WindowBg;
                control.ForeColor = JoydexTheme.Text;
                childBackground = JoydexTheme.WindowBg;
                break;
            case CardPanel:
                control.BackColor = JoydexTheme.Surface;
                control.ForeColor = JoydexTheme.Text;
                childBackground = JoydexTheme.Surface;
                break;
            case TabPage:
                control.BackColor = JoydexTheme.WindowBg;
                control.ForeColor = JoydexTheme.Text;
                childBackground = JoydexTheme.WindowBg;
                break;
            case BorderedTextBox:
                control.BackColor = JoydexTheme.InputBg;
                control.ForeColor = JoydexTheme.Text;
                childBackground = JoydexTheme.InputBg;
                break;
            case TableLayoutPanel or FlowLayoutPanel or Panel:
                control.BackColor = inheritedBackground;
                control.ForeColor = JoydexTheme.Text;
                childBackground = inheritedBackground;
                break;
            case ModernDataGridView modernGrid:
                ApplyGrid(modernGrid);
                break;
            case DataGridView grid:
                ApplyGrid(grid);
                break;
            case RoundedButton:
                control.BackColor = inheritedBackground;
                control.ForeColor = JoydexTheme.Text;
                break;
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = JoydexTheme.Border;
                button.FlatAppearance.BorderSize = 1;
                button.BackColor = JoydexTheme.Surface;
                button.ForeColor = JoydexTheme.Text;
                button.UseVisualStyleBackColor = false;
                break;
            case TextBoxBase textBox:
                textBox.BackColor = JoydexTheme.InputBg;
                textBox.ForeColor = JoydexTheme.Text;
                textBox.BorderStyle = textBox.Parent is BorderedTextBox
                    ? BorderStyle.None
                    : BorderStyle.FixedSingle;
                break;
            case ComboBox comboBox:
                comboBox.BackColor = JoydexTheme.InputBg;
                comboBox.ForeColor = JoydexTheme.Text;
                comboBox.FlatStyle = FlatStyle.Flat;
                comboBox.DrawMode = DrawMode.OwnerDrawFixed;
                if (!StyledComboBoxes.TryGetValue(comboBox, out _))
                {
                    StyledComboBoxes.Add(comboBox, new object());
                    comboBox.DrawItem += DrawComboBoxItem;
                }
                break;
            case NumericUpDown numeric:
                numeric.BackColor = JoydexTheme.InputBg;
                numeric.ForeColor = JoydexTheme.Text;
                numeric.BorderStyle = BorderStyle.FixedSingle;
                break;
            case PromptListBox promptList:
                promptList.ApplyTheme();
                break;
            case ListBox listBox:
                listBox.BackColor = JoydexTheme.Surface;
                listBox.ForeColor = JoydexTheme.Text;
                listBox.BorderStyle = BorderStyle.None;
                break;
            case ListView listView:
                listView.BackColor = JoydexTheme.Surface;
                listView.ForeColor = JoydexTheme.Text;
                listView.BorderStyle = BorderStyle.None;
                break;
            case CheckBox checkBox:
                checkBox.BackColor = inheritedBackground;
                checkBox.ForeColor = JoydexTheme.Text;
                checkBox.FlatStyle = FlatStyle.Flat;
                break;
            case Label label:
                label.BackColor = inheritedBackground;
                if (label.Tag is ThemeTone tone)
                {
                    label.ForeColor = tone switch
                    {
                        ThemeTone.Subtle => JoydexTheme.TextSub,
                        ThemeTone.Faint => JoydexTheme.TextFaint,
                        ThemeTone.Accent => JoydexTheme.AccentText,
                        ThemeTone.Success => JoydexTheme.Success,
                        _ => JoydexTheme.Text,
                    };
                }
                else if (label.ForeColor == SystemColors.GrayText || label.ForeColor == Color.DarkGray)
                {
                    label.Tag = ThemeTone.Subtle;
                    label.ForeColor = JoydexTheme.TextSub;
                }
                else if (label.ForeColor == Color.DarkBlue)
                {
                    label.Tag = ThemeTone.Accent;
                    label.ForeColor = JoydexTheme.AccentText;
                }
                else
                {
                    label.ForeColor = JoydexTheme.Text;
                }
                break;
            case GroupBox groupBox:
                groupBox.BackColor = JoydexTheme.Surface;
                groupBox.ForeColor = JoydexTheme.Text;
                childBackground = JoydexTheme.Surface;
                break;
            default:
                control.ForeColor = JoydexTheme.Text;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyControl(child, childBackground);
        }
    }

    private static void DrawComboBoxItem(object? sender, DrawItemEventArgs eventArgs)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        var selected = (eventArgs.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? JoydexTheme.AccentTint : JoydexTheme.InputBg);
        eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
        var text = eventArgs.Index >= 0 && eventArgs.Index < comboBox.Items.Count
            ? comboBox.GetItemText(comboBox.Items[eventArgs.Index])
            : comboBox.Text;
        TextRenderer.DrawText(
            eventArgs.Graphics,
            text,
            comboBox.Font,
            new Rectangle(
                eventArgs.Bounds.Left + 4,
                eventArgs.Bounds.Top,
                Math.Max(0, eventArgs.Bounds.Width - 8),
                eventArgs.Bounds.Height),
            JoydexTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if ((eventArgs.State & DrawItemState.Focus) != 0)
        {
            eventArgs.DrawFocusRectangle();
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}

/// <summary>
/// Base form that tracks Windows app-theme changes without recreating its handle.
/// </summary>
internal class ThemedForm : Form
{
    private const int WmSettingChange = 0x001A;
    private bool _initialAutoScalePending = true;
    private Size? _logicalMinimumSize;

    internal bool SuppressActivation { get; set; }

    protected override bool ShowWithoutActivation => SuppressActivation || base.ShowWithoutActivation;

    protected ThemedForm()
    {
        // Keep ContainerControl from consuming the 96-DPI baseline when the handle
        // first establishes its real monitor DPI. The complete derived tree is scaled
        // once in OnHandleCreated instead.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = JoydexTheme.UiFont;
        BackColor = JoydexTheme.WindowBg;
        ForeColor = JoydexTheme.Text;
    }

    protected void SetLogicalMinimumSize(Size minimumSize)
    {
        _logicalMinimumSize = minimumSize;
        MinimumSize = minimumSize;
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        if (_initialAutoScalePending)
        {
            _initialAutoScalePending = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            // Switching from None clears the authored baseline.
            AutoScaleDimensions = new SizeF(96F, 96F);
            ResumeLayout(performLayout: false);
            PerformLayout();
        }

        ApplyCurrentTheme();
        ReflowForCurrentDpi();
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        ApplyCurrentTheme();
        ReflowForCurrentDpi();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        ApplyCurrentTheme();
        ReflowForCurrentDpi();
        if (IsHandleCreated && !IsDisposed && !Disposing)
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && !Disposing)
                {
                    ApplyCurrentTheme();
                    ReflowForCurrentDpi();
                }
            });
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmSettingChange)
        {
            var setting = message.LParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(message.LParam);
            if (string.IsNullOrEmpty(setting)
                || string.Equals(setting, "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase))
            {
                JoydexTheme.RefreshSystemPreference();
                ApplyCurrentTheme();
            }
        }

        base.WndProc(ref message);
    }

    protected virtual void OnThemeApplied()
    {
    }

    private void ApplyCurrentTheme()
    {
        ThemeService.Apply(this);
        OnThemeApplied();
    }

    private void ReflowForCurrentDpi()
    {
        SuspendLayout();
        try
        {
            if (_logicalMinimumSize is { } logicalMinimum)
            {
                var workingArea = Screen.FromHandle(Handle).WorkingArea;
                MinimumSize = SuppressActivation
                    ? logicalMinimum
                    : new Size(
                        Math.Min(JoydexTheme.ScaleLogical(logicalMinimum.Width, DeviceDpi), workingArea.Width),
                        Math.Min(JoydexTheme.ScaleLogical(logicalMinimum.Height, DeviceDpi), workingArea.Height));
            }

            PerformLayout();
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }

        if (SuppressActivation || WindowState != FormWindowState.Normal)
        {
            return;
        }

        var area = Screen.FromHandle(Handle).WorkingArea;
        var width = Math.Min(Math.Max(MinimumSize.Width, Width), area.Width);
        var height = Math.Min(Math.Max(MinimumSize.Height, Height), area.Height);
        var left = Math.Clamp(Left, area.Left, area.Right - width);
        var top = Math.Clamp(Top, area.Top, area.Bottom - height);
        Bounds = new Rectangle(left, top, width, height);
    }
}
