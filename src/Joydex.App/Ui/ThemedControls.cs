using System.Drawing.Drawing2D;

namespace Joydex.App;

internal enum ButtonVariant
{
    Secondary,
    Primary,
    Ghost,
}

/// <summary>
/// Button with modern Joydex colors while retaining WinForms dialog and keyboard behavior.
/// </summary>
internal sealed class RoundedButton : Button
{
    private bool _hovered;
    private bool _pressed;
    private bool _isDefault;
    private ButtonVariant _variant;
    private int _cornerRadius = 6;

    public RoundedButton()
    {
        AutoSize = true;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        MinimumSize = new Size(0, JoydexTheme.StandardControlHeight);
        Padding = new Padding(12, 7, 12, 7);
        UseVisualStyleBackColor = false;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    public ButtonVariant Variant
    {
        get => _variant;
        set
        {
            if (_variant == value)
            {
                return;
            }

            _variant = value;
            Parent?.PerformLayout(this, nameof(Variant));
            Invalidate();
        }
    }

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(0, value);
            UpdateButtonRegion();
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var preferredFont = Variant == ButtonVariant.Primary ? JoydexTheme.UiSemiboldFont : Font;
        var text = TextRenderer.MeasureText(
            Text,
            preferredFont,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        return new Size(
            Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal + JoydexTheme.ScaleLogical(4, DeviceDpi)),
            Math.Max(MinimumSize.Height, text.Height + Padding.Vertical + JoydexTheme.ScaleLogical(4, DeviceDpi)));
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        UpdateButtonRegion();
        Parent?.PerformLayout(this, nameof(DeviceDpi));
        Invalidate();
    }

    public override void NotifyDefault(bool value)
    {
        _isDefault = value;
        Invalidate();
        base.NotifyDefault(value);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        _pressed = eventArgs.Button == MouseButtons.Left;
        Invalidate();
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(eventArgs);
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        Invalidate();
        base.OnEnabledChanged(eventArgs);
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        UpdateButtonRegion();
    }

    private void UpdateButtonRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var bounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        var scaledRadius = JoydexTheme.ScaleLogical(CornerRadius, DeviceDpi);
        using var path = ThemeDrawing.RoundedRectangle(bounds, scaledRadius);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        var scaledRadius = JoydexTheme.ScaleLogical(CornerRadius, DeviceDpi);
        using var path = ThemeDrawing.RoundedRectangle(bounds, scaledRadius);
        var (fill, border, text) = Colors();
        using var fillBrush = new SolidBrush(fill);
        eventArgs.Graphics.FillPath(fillBrush, path);
        if (border != Color.Transparent)
        {
            var borderWidth = Math.Max(1F, DeviceDpi / 96F);
            var borderInset = Math.Max(1, (int)Math.Ceiling(borderWidth / 2F));
            var borderBounds = Rectangle.Inflate(bounds, -borderInset, -borderInset);
            using var borderPath = ThemeDrawing.RoundedRectangle(
                borderBounds,
                Math.Max(0, scaledRadius - borderInset));
            using var borderPen = new Pen(border, borderWidth);
            eventArgs.Graphics.DrawPath(borderPen, borderPath);
        }

        var flags = TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Variant == ButtonVariant.Primary ? JoydexTheme.UiSemiboldFont : Font,
            ClientRectangle,
            text,
            flags);

        if ((Focused && ShowFocusCues) || _isDefault)
        {
            var focusBounds = Rectangle.Inflate(bounds, -3, -3);
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, focusBounds, text, fill);
        }
    }

    private (Color Fill, Color Border, Color Text) Colors()
    {
        if (!Enabled)
        {
            return (JoydexTheme.DisabledBg, JoydexTheme.BorderSoft, JoydexTheme.DisabledText);
        }

        var shift = _pressed ? -0.14F : _hovered ? -0.08F : 0F;
        return Variant switch
        {
            ButtonVariant.Primary => (
                JoydexTheme.Shift(JoydexTheme.Accent, shift),
                Color.Transparent,
                JoydexTheme.PrimaryText),
            ButtonVariant.Ghost => (
                _hovered || _pressed ? JoydexTheme.HoverBg : JoydexTheme.Surface,
                Color.Transparent,
                JoydexTheme.TextSub),
            _ => (
                _hovered || _pressed ? JoydexTheme.HoverBg : JoydexTheme.Surface,
                _isDefault ? JoydexTheme.Accent : JoydexTheme.Border,
                JoydexTheme.Text),
        };
    }
}

/// <summary>
/// Compact borderless editor hosted inside a painted modern input surface.
/// </summary>
internal sealed class BorderedTextBox : Panel
{
    private const int PreferredLogicalHeight = JoydexTheme.StandardControlHeight + 8;
    private readonly Label _placeholder;

    public BorderedTextBox()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = JoydexTheme.InputBg;
        Padding = new Padding(10, 6, 10, 5);
        Size = new Size(280, PreferredLogicalHeight);
        MinimumSize = new Size(120, PreferredLogicalHeight);
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        Editor = new TextBox
        {
            AcceptsReturn = false,
            BackColor = JoydexTheme.InputBg,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            ForeColor = JoydexTheme.Text,
            Multiline = true,
            ScrollBars = ScrollBars.None,
            WordWrap = false,
        };
        _placeholder = new Label
        {
            Cursor = Cursors.IBeam,
            Dock = DockStyle.Fill,
            Padding = new Padding(1, 0, 0, 0),
            Tag = ThemeTone.Faint,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Editor.Enter += (_, _) =>
        {
            _placeholder.Visible = false;
            Invalidate();
        };
        Editor.Leave += (_, _) =>
        {
            _placeholder.Visible = Editor.TextLength == 0;
            Invalidate();
        };
        Editor.TextChanged += (_, _) => _placeholder.Visible = Editor.TextLength == 0 && !Editor.Focused;
        Editor.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Enter)
            {
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
            }
        };
        Controls.Add(Editor);
        Controls.Add(_placeholder);
        _placeholder.BringToFront();
        _placeholder.Click += (_, _) => Editor.Focus();
        Click += (_, _) => Editor.Focus();
    }

    public TextBox Editor { get; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var contentHeight = Editor.Font.Height + Padding.Vertical + 6;
        return new Size(
            Math.Max(MinimumSize.Width, Width),
            Math.Max(MinimumSize.Height, contentHeight));
    }

    public string PlaceholderText
    {
        get => _placeholder.Text;
        set
        {
            _placeholder.Text = value;
            _placeholder.Visible = Editor.TextLength == 0 && !Editor.Focused;
        }
    }

    internal void ShowPlaceholderForDocumentation()
    {
        if (Editor.TextLength != 0)
        {
            return;
        }

        // DrawToBitmap can composite sibling HWND controls in reverse z-order.
        // Hide the empty native editor so the authored placeholder remains visible.
        Editor.Visible = false;
        _placeholder.Visible = true;
        _placeholder.BringToFront();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using var path = ThemeDrawing.RoundedRectangle(bounds, 6);
        using var borderPath = ThemeDrawing.RoundedRectangle(Rectangle.Inflate(bounds, -1, -1), 5);
        using var fill = new SolidBrush(JoydexTheme.InputBg);
        using var border = new Pen(Editor.Focused ? JoydexTheme.Accent : JoydexTheme.Border);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(border, borderPath);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        Parent?.PerformLayout(this, nameof(DeviceDpi));
        PerformLayout();
        Invalidate();
    }
}

/// <summary>
/// Rounded surface used to group related controls without native GroupBox chrome.
/// </summary>
internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        BackColor = JoydexTheme.Surface;
        Padding = new Padding(16);
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(Parent?.BackColor ?? JoydexTheme.WindowBg);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using var path = ThemeDrawing.RoundedRectangle(bounds, JoydexTheme.ScaleLogical(8, DeviceDpi));
        using var brush = new SolidBrush(JoydexTheme.Surface);
        eventArgs.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using var path = ThemeDrawing.RoundedRectangle(bounds, JoydexTheme.ScaleLogical(8, DeviceDpi));
        using var pen = new Pen(JoydexTheme.Border);
        eventArgs.Graphics.DrawPath(pen, path);
    }
}

/// <summary>
/// Small semantic status indicator that remains visible across themes.
/// </summary>
internal sealed class StatusDot : Control
{
    public StatusDot()
    {
        AccessibleName = "Ready";
        AccessibleRole = AccessibleRole.Indicator;
        Margin = new Padding(0, 11, 8, 0);
        Size = new Size(8, 8);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(JoydexTheme.Success);
        eventArgs.Graphics.FillEllipse(brush, ClientRectangle);
    }
}

/// <summary>
/// Announces changing capture and input status through WinForms accessibility events.
/// </summary>
internal sealed class StatusLabel : Label
{
    public StatusLabel()
    {
        AccessibleName = "Controller capture status";
        AccessibleRole = AccessibleRole.StatusBar;
        AutoSize = true;
    }

    protected override void OnTextChanged(EventArgs eventArgs)
    {
        base.OnTextChanged(eventArgs);
        if (IsHandleCreated)
        {
            AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        }
    }
}

/// <summary>
/// Sidebar page selector with radio-style keyboard navigation.
/// </summary>
internal enum NavGlyph
{
    None,
    Bindings,
    PromptPickers,
    ButtonMaps,
    General,
}

internal sealed class NavButton : Control
{
    private bool _hovered;
    private bool _selected;

    public NavButton()
    {
        AccessibleRole = AccessibleRole.PageTab;
        Cursor = Cursors.Hand;
        Font = JoydexTheme.UiFont;
        Height = 36;
        Margin = new Padding(0, 2, 0, 2);
        TabStop = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.Selectable
            | ControlStyles.ResizeRedraw,
            true);
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    public Image? Icon { get; set; }

    public NavGlyph Glyph { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var hasGlyph = Icon is not null || Glyph != NavGlyph.None;
        var textLeft = JoydexTheme.ScaleLogical(hasGlyph ? 38 : 12, DeviceDpi);
        var textSize = TextRenderer.MeasureText(
            Text,
            Selected ? JoydexTheme.UiSemiboldFont : Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        return new Size(
            textLeft + textSize.Width + JoydexTheme.ScaleLogical(32, DeviceDpi),
            Math.Max(
                JoydexTheme.ScaleLogical(36, DeviceDpi),
                textSize.Height + JoydexTheme.ScaleLogical(12, DeviceDpi)));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavButtonAccessibleObject(this);

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        return key is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End
            || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            eventArgs.Handled = true;
        }
        else if (eventArgs.KeyCode is Keys.Up or Keys.Left)
        {
            MoveFocus(-1);
            eventArgs.Handled = true;
        }
        else if (eventArgs.KeyCode is Keys.Down or Keys.Right)
        {
            MoveFocus(1);
            eventArgs.Handled = true;
        }
        else if (eventArgs.KeyCode == Keys.Home)
        {
            MoveFocus(int.MinValue);
            eventArgs.Handled = true;
        }
        else if (eventArgs.KeyCode == Keys.End)
        {
            MoveFocus(int.MaxValue);
            eventArgs.Handled = true;
        }

        base.OnKeyDown(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96F;
        int Scale(int value) => (int)Math.Round(value * scale);
        var bounds = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        if (Selected || _hovered)
        {
            using var path = ThemeDrawing.RoundedRectangle(bounds, Scale(6));
            using var brush = new SolidBrush(Selected ? JoydexTheme.AccentTint : JoydexTheme.HoverBg);
            eventArgs.Graphics.FillPath(brush, path);
        }

        if (Selected)
        {
            using var barPath = ThemeDrawing.RoundedRectangle(
                new Rectangle(0, Scale(9), Math.Max(1, Scale(3)), Math.Max(Scale(2), Height - Scale(18))),
                Scale(2));
            using var barBrush = new SolidBrush(JoydexTheme.Accent);
            eventArgs.Graphics.FillPath(barBrush, barPath);
        }

        var hasGlyph = Icon is not null || Glyph != NavGlyph.None;
        var textLeft = Scale(hasGlyph ? 38 : 12);
        if (Icon is not null)
        {
            var iconSize = Scale(16);
            eventArgs.Graphics.DrawImage(Icon, new Rectangle(Scale(12), (Height - iconSize) / 2, iconSize, iconSize));
        }
        else if (Glyph != NavGlyph.None)
        {
            var glyphSize = Scale(16);
            DrawGlyph(
                eventArgs.Graphics,
                new Rectangle(Scale(12), (Height - glyphSize) / 2, glyphSize, glyphSize),
                Selected ? JoydexTheme.AccentText : JoydexTheme.TextSub);
        }

        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Selected ? JoydexTheme.UiSemiboldFont : Font,
            new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft - Scale(8)), Height),
            Selected ? JoydexTheme.AccentText : JoydexTheme.TextSub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(bounds, -Scale(4), -Scale(4)));
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        Parent?.PerformLayout(this, nameof(DeviceDpi));
        Invalidate();
    }

    private void DrawGlyph(Graphics graphics, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, 1.4F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        var left = bounds.Left + 2;
        var top = bounds.Top + 2;
        var right = bounds.Right - 2;
        var bottom = bounds.Bottom - 2;

        switch (Glyph)
        {
            case NavGlyph.Bindings:
                using (var table = ThemeDrawing.RoundedRectangle(
                           new Rectangle(left, top + 1, right - left, bottom - top - 2),
                           2))
                {
                    graphics.DrawPath(pen, table);
                }
                graphics.DrawLine(pen, left, top + 5, right, top + 5);
                graphics.DrawLine(pen, left + 4, top + 5, left + 4, bottom - 1);
                break;
            case NavGlyph.PromptPickers:
                using (var bubble = ThemeDrawing.RoundedRectangle(
                           new Rectangle(left, top, right - left, bottom - top - 3),
                           3))
                {
                    graphics.DrawPath(pen, bubble);
                }
                graphics.DrawLines(pen, new Point[]
                {
                    new Point(left + 4, bottom - 3),
                    new Point(left + 3, bottom),
                    new Point(left + 7, bottom - 3),
                });
                break;
            case NavGlyph.ButtonMaps:
                for (var row = 0; row < 2; row++)
                {
                    for (var column = 0; column < 2; column++)
                    {
                        graphics.DrawEllipse(pen, left + (column * 7), top + (row * 7), 4, 4);
                    }
                }
                break;
            case NavGlyph.General:
                graphics.DrawLine(pen, left, top + 2, right, top + 2);
                graphics.DrawLine(pen, left, top + 7, right, top + 7);
                graphics.DrawLine(pen, left, top + 12, right, top + 12);
                graphics.DrawEllipse(pen, left + 3, top, 4, 4);
                graphics.DrawEllipse(pen, right - 7, top + 5, 4, 4);
                graphics.DrawEllipse(pen, left + 5, top + 10, 4, 4);
                break;
        }
    }

    private void MoveFocus(int delta)
    {
        var siblings = Parent?.Controls.OfType<NavButton>().Where(button => button.Enabled && button.Visible).ToList();
        if (siblings is not { Count: > 0 })
        {
            return;
        }

        var current = siblings.IndexOf(this);
        var target = delta switch
        {
            int.MinValue => 0,
            int.MaxValue => siblings.Count - 1,
            _ => (current + delta + siblings.Count) % siblings.Count,
        };
        siblings[target].Focus();
        siblings[target].OnClick(EventArgs.Empty);
    }

    private sealed class NavButtonAccessibleObject(NavButton owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State
            | (owner.Selected ? AccessibleStates.Selected | AccessibleStates.Checked : AccessibleStates.None);
    }
}

/// <summary>
/// Themed grid that paints a slim accent on the selected row.
/// </summary>
internal sealed class ModernDataGridView : DataGridView
{
    private ComboBox? _editingComboBox;

    public ModernDataGridView()
    {
        DoubleBuffered = true;
        ThemeService.ApplyGrid(this);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        ThemeService.ApplyGrid(this);
    }

    protected override void OnColumnAdded(DataGridViewColumnEventArgs eventArgs)
    {
        base.OnColumnAdded(eventArgs);
        eventArgs.Column.DividerWidth = 0;
        NormalizeComboColumn(eventArgs.Column);
    }

    protected override void OnRowsAdded(DataGridViewRowsAddedEventArgs eventArgs)
    {
        base.OnRowsAdded(eventArgs);
        var lastRow = Math.Min(Rows.Count, eventArgs.RowIndex + eventArgs.RowCount);
        for (var rowIndex = eventArgs.RowIndex; rowIndex < lastRow; rowIndex++)
        {
            Rows[rowIndex].DividerHeight = 0;
            foreach (DataGridViewCell cell in Rows[rowIndex].Cells)
            {
                NormalizeComboCell(cell);
            }
        }
    }

    protected override void OnCurrentCellChanged(EventArgs eventArgs)
    {
        base.OnCurrentCellChanged(eventArgs);
        Invalidate();
    }

    protected override void OnCellEndEdit(DataGridViewCellEventArgs eventArgs)
    {
        base.OnCellEndEdit(eventArgs);
        Invalidate();
    }

    protected override void OnCellMouseEnter(DataGridViewCellEventArgs eventArgs)
    {
        base.OnCellMouseEnter(eventArgs);
        if (eventArgs.ColumnIndex >= 0 && eventArgs.RowIndex >= 0)
        {
            InvalidateCell(eventArgs.ColumnIndex, eventArgs.RowIndex);
        }
    }

    protected override void OnCellMouseLeave(DataGridViewCellEventArgs eventArgs)
    {
        base.OnCellMouseLeave(eventArgs);
        if (eventArgs.ColumnIndex >= 0 && eventArgs.RowIndex >= 0)
        {
            InvalidateCell(eventArgs.ColumnIndex, eventArgs.RowIndex);
        }
    }

    protected override void OnCellMouseClick(DataGridViewCellMouseEventArgs eventArgs)
    {
        base.OnCellMouseClick(eventArgs);
        if (eventArgs.Button != MouseButtons.Left
            || eventArgs.Clicks != 1
            || eventArgs.RowIndex < 0
            || eventArgs.ColumnIndex < 0
            || ReadOnly
            || Rows[eventArgs.RowIndex].Cells[eventArgs.ColumnIndex] is not DataGridViewComboBoxCell { ReadOnly: false } cell)
        {
            return;
        }

        CurrentCell = cell;
        if (BeginEdit(selectAll: false) && EditingControl is DataGridViewComboBoxEditingControl comboBox)
        {
            comboBox.DroppedDown = true;
        }
    }

    protected override void OnCurrentCellDirtyStateChanged(EventArgs eventArgs)
    {
        base.OnCurrentCellDirtyStateChanged(eventArgs);
        if (IsCurrentCellDirty && CurrentCell is DataGridViewComboBoxCell)
        {
            CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    protected override void OnScroll(ScrollEventArgs eventArgs)
    {
        base.OnScroll(eventArgs);
        Invalidate();
    }

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs eventArgs)
    {
        base.OnCellPainting(eventArgs);
        if (eventArgs.Handled || eventArgs.ColumnIndex < 0 || eventArgs.Graphics is not { } graphics)
        {
            return;
        }

        if (eventArgs.RowIndex < 0)
        {
            PaintCellSurface(graphics, eventArgs, selected: false);
            DrawCellValue(graphics, eventArgs, reserveRight: 0);
            using var separator = new SolidBrush(GridColor);
            var graphicsState = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.FillRectangle(
                    separator,
                    eventArgs.CellBounds.Left,
                    eventArgs.CellBounds.Bottom - 1,
                    eventArgs.CellBounds.Width,
                    1);
            }
            finally
            {
                graphics.Restore(graphicsState);
            }
            eventArgs.Handled = true;
            return;
        }

        var comboColumn = Columns[eventArgs.ColumnIndex] is DataGridViewComboBoxColumn;
        var current = CurrentCell;
        if (comboColumn
            && IsCurrentCellInEditMode
            && current is not null
            && current.RowIndex == eventArgs.RowIndex
            && current.ColumnIndex == eventArgs.ColumnIndex)
        {
            return;
        }

        var selected = (eventArgs.State & DataGridViewElementStates.Selected) != 0;
        var arrowWidth = comboColumn ? JoydexTheme.ScaleLogical(24, DeviceDpi) : 0;
        PaintCellSurface(graphics, eventArgs, selected);
        DrawCellValue(graphics, eventArgs, arrowWidth);
        if (comboColumn)
        {
            TextRenderer.DrawText(
                graphics,
                "\u25BE",
                JoydexTheme.SectionFont,
                new Rectangle(
                    eventArgs.CellBounds.Right - arrowWidth,
                    eventArgs.CellBounds.Top,
                    arrowWidth,
                    eventArgs.CellBounds.Height),
                JoydexTheme.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        eventArgs.Handled = true;
    }

    protected override void OnEditingControlShowing(DataGridViewEditingControlShowingEventArgs eventArgs)
    {
        base.OnEditingControlShowing(eventArgs);
        if (_editingComboBox is not null)
        {
            _editingComboBox.SelectionChangeCommitted -= OnComboSelectionChangeCommitted;
            _editingComboBox = null;
        }

        if (eventArgs.Control is not ComboBox comboBox)
        {
            return;
        }

        _editingComboBox = comboBox;
        comboBox.BackColor = JoydexTheme.InputBg;
        comboBox.ForeColor = JoydexTheme.Text;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.Font = eventArgs.CellStyle.Font ?? Font;
        comboBox.ItemHeight = Math.Max(
            JoydexTheme.ScaleLogical(24, DeviceDpi),
            comboBox.Font.Height + JoydexTheme.ScaleLogical(6, DeviceDpi));
        comboBox.SelectionChangeCommitted += OnComboSelectionChangeCommitted;
    }

    private void OnComboSelectionChangeCommitted(object? sender, EventArgs eventArgs)
    {
        if (IsCurrentCellDirty)
        {
            CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        EndEdit();
    }

    private static void NormalizeComboColumn(DataGridViewColumn column)
    {
        if (column is not DataGridViewComboBoxColumn comboColumn)
        {
            return;
        }

        comboColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing;
        comboColumn.DisplayStyleForCurrentCellOnly = false;
        comboColumn.FlatStyle = FlatStyle.Flat;
    }

    private static void NormalizeComboCell(DataGridViewCell cell)
    {
        if (cell is not DataGridViewComboBoxCell comboCell)
        {
            return;
        }

        comboCell.DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing;
        comboCell.DisplayStyleForCurrentCellOnly = false;
        comboCell.FlatStyle = FlatStyle.Flat;
    }

    private void PaintCellSurface(
        Graphics graphics,
        DataGridViewCellPaintingEventArgs eventArgs,
        bool selected)
    {
        var style = eventArgs.CellStyle ?? DefaultCellStyle;
        using var background = new SolidBrush(selected ? style.SelectionBackColor : style.BackColor);
        graphics.FillRectangle(background, eventArgs.CellBounds);
    }

    private void DrawCellValue(
        Graphics graphics,
        DataGridViewCellPaintingEventArgs eventArgs,
        int reserveRight)
    {
        var value = Convert.ToString(eventArgs.FormattedValue) ?? string.Empty;
        if (value.Length == 0)
        {
            return;
        }

        var style = eventArgs.CellStyle ?? DefaultCellStyle;
        var selected = (eventArgs.State & DataGridViewElementStates.Selected) != 0;
        var textBounds = new Rectangle(
            eventArgs.CellBounds.Left + style.Padding.Left,
            eventArgs.CellBounds.Top + style.Padding.Top,
            Math.Max(0, eventArgs.CellBounds.Width - style.Padding.Horizontal - reserveRight),
            Math.Max(0, eventArgs.CellBounds.Height - style.Padding.Vertical));
        TextRenderer.DrawText(
            graphics,
            value,
            style.Font ?? Font,
            textBounds,
            selected ? style.SelectionForeColor : style.ForeColor,
            TextFlags(style.Alignment));
    }

    private static TextFormatFlags TextFlags(DataGridViewContentAlignment alignment)
    {
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        flags |= alignment switch
        {
            DataGridViewContentAlignment.TopCenter
                or DataGridViewContentAlignment.MiddleCenter
                or DataGridViewContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
            DataGridViewContentAlignment.TopRight
                or DataGridViewContentAlignment.MiddleRight
                or DataGridViewContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left,
        };
        flags |= alignment switch
        {
            DataGridViewContentAlignment.TopLeft
                or DataGridViewContentAlignment.TopCenter
                or DataGridViewContentAlignment.TopRight => TextFormatFlags.Top,
            DataGridViewContentAlignment.BottomLeft
                or DataGridViewContentAlignment.BottomCenter
                or DataGridViewContentAlignment.BottomRight => TextFormatFlags.Bottom,
            _ => TextFormatFlags.VerticalCenter,
        };
        return flags;
    }

    protected override void OnRowPostPaint(DataGridViewRowPostPaintEventArgs eventArgs)
    {
        base.OnRowPostPaint(eventArgs);
        var graphicsState = eventArgs.Graphics.Save();
        try
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.None;
            using var separator = new SolidBrush(GridColor);
            eventArgs.Graphics.FillRectangle(
                separator,
                eventArgs.RowBounds.Left,
                eventArgs.RowBounds.Bottom - 1,
                eventArgs.RowBounds.Width,
                1);
            if (Rows[eventArgs.RowIndex].Selected)
            {
                using var accent = new SolidBrush(JoydexTheme.Accent);
                eventArgs.Graphics.FillRectangle(
                    accent,
                    eventArgs.RowBounds.Left,
                    eventArgs.RowBounds.Top,
                    2,
                    eventArgs.RowBounds.Height);
            }
        }
        finally
        {
            eventArgs.Graphics.Restore(graphicsState);
        }
    }
}

/// <summary>
/// Prompt list with selection treatment and compact metadata pills.
/// </summary>
internal sealed class PromptListBox : ListBox
{
    public PromptListBox()
    {
        BorderStyle = BorderStyle.None;
        DrawMode = DrawMode.OwnerDrawFixed;
        IntegralHeight = false;
        UpdateItemHeight();
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = JoydexTheme.Surface;
        ForeColor = JoydexTheme.Text;
        Invalidate();
    }

    protected override void OnDrawItem(DrawItemEventArgs eventArgs)
    {
        if (eventArgs.Index < 0 || eventArgs.Index >= Items.Count)
        {
            return;
        }

        var selected = (eventArgs.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? JoydexTheme.AccentTint : JoydexTheme.Surface);
        eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(JoydexTheme.Accent);
            eventArgs.Graphics.FillRectangle(
                accent,
                eventArgs.Bounds.Left,
                eventArgs.Bounds.Top,
                JoydexTheme.ScaleLogical(2, DeviceDpi),
                eventArgs.Bounds.Height);
        }

        var value = GetItemText(Items[eventArgs.Index]) ?? string.Empty;
        var isDefault = value.StartsWith("★ ", StringComparison.Ordinal);
        var submits = value.EndsWith(" [+ Submit]", StringComparison.Ordinal);
        var text = value;
        if (isDefault)
        {
            text = text[2..];
        }
        if (submits)
        {
            text = text[..^" [+ Submit]".Length];
        }

        var horizontalInset = JoydexTheme.ScaleLogical(10, DeviceDpi);
        var pillGap = JoydexTheme.ScaleLogical(6, DeviceDpi);
        var left = eventArgs.Bounds.Left + horizontalInset;
        var right = eventArgs.Bounds.Right - horizontalInset;
        if (submits)
        {
            right = DrawPill(
                eventArgs.Graphics,
                "+ SUBMIT",
                right,
                eventArgs.Bounds,
                JoydexTheme.AccentTint,
                JoydexTheme.AccentText) - pillGap;
        }
        if (isDefault)
        {
            right = DrawPill(
                eventArgs.Graphics,
                "★ DEFAULT",
                right,
                eventArgs.Bounds,
                JoydexTheme.TagWarnBg,
                JoydexTheme.TagWarnText) - pillGap;
        }

        TextRenderer.DrawText(
            eventArgs.Graphics,
            text,
            Font,
            new Rectangle(left, eventArgs.Bounds.Top, Math.Max(0, right - left), eventArgs.Bounds.Height),
            selected ? JoydexTheme.Text : JoydexTheme.TextSub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        eventArgs.DrawFocusRectangle();
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        UpdateItemHeight();
        Invalidate();
    }

    private void UpdateItemHeight()
    {
        ItemHeight = Math.Max(
            JoydexTheme.ScaleLogical(38, DeviceDpi),
            Font.Height + JoydexTheme.ScaleLogical(12, DeviceDpi));
    }

    private int DrawPill(
        Graphics graphics,
        string text,
        int right,
        Rectangle rowBounds,
        Color background,
        Color foreground)
    {
        var textSize = TextRenderer.MeasureText(text, JoydexTheme.SectionFont, Size.Empty, TextFormatFlags.NoPadding);
        var horizontalPadding = JoydexTheme.ScaleLogical(14, DeviceDpi);
        var height = JoydexTheme.ScaleLogical(20, DeviceDpi);
        var bounds = new Rectangle(
            right - textSize.Width - horizontalPadding,
            rowBounds.Top + ((rowBounds.Height - height) / 2),
            textSize.Width + horizontalPadding,
            height);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ThemeDrawing.RoundedRectangle(bounds, height / 2);
        using var brush = new SolidBrush(background);
        graphics.FillPath(brush, path);
        TextRenderer.DrawText(
            graphics,
            text,
            JoydexTheme.SectionFont,
            bounds,
            foreground,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return bounds.Left;
    }
}

internal static class ThemeDrawing
{
    public static void FillRoundedRectangle(Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRectangle(bounds, radius);
            graphics.FillPath(brush, path);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
