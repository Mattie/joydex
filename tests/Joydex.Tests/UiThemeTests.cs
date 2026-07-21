using Joydex.App;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Joydex.Tests;

public sealed class UiThemeTests
{
    [Fact]
    public void DarkModeOverridesNestAndRestoreThePreviousPalette()
    {
        var initial = JoydexTheme.Dark;

        using (JoydexTheme.OverrideDarkMode(!initial))
        {
            Assert.Equal(!initial, JoydexTheme.Dark);
            using (JoydexTheme.OverrideDarkMode(initial))
            {
                Assert.Equal(initial, JoydexTheme.Dark);
            }

            Assert.Equal(!initial, JoydexTheme.Dark);
        }

        Assert.Equal(initial, JoydexTheme.Dark);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GridThemeUsesOpaqueColorsAndPreservesEditableData(bool dark)
    {
        using var theme = JoydexTheme.OverrideDarkMode(dark);
        using var grid = new ModernDataGridView
        {
            AllowUserToAddRows = false,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Label", Name = "Label" });
        var comboColumn = new DataGridViewComboBoxColumn { HeaderText = "Choice", Name = "Choice" };
        comboColumn.Items.Add("Keep me flat");
        grid.Columns.Add(comboColumn);
        grid.Rows.Add("Keep me", "Keep me flat");

        ThemeService.ApplyGrid(grid);

        Assert.Equal("LABEL", grid.Columns[0].HeaderText);
        Assert.Equal("Keep me", grid.Rows[0].Cells[0].Value);
        Assert.Equal(255, grid.DefaultCellStyle.SelectionBackColor.A);
        Assert.Equal(DataGridViewCellBorderStyle.None, grid.CellBorderStyle);
        Assert.Equal(DataGridViewHeaderBorderStyle.None, grid.ColumnHeadersBorderStyle);
        Assert.All(grid.Columns.Cast<DataGridViewColumn>(), column => Assert.Equal(0, column.DividerWidth));
        Assert.All(grid.Rows.Cast<DataGridViewRow>(), row => Assert.Equal(0, row.DividerHeight));
        Assert.Equal(DataGridViewComboBoxDisplayStyle.Nothing, comboColumn.DisplayStyle);
        Assert.False(comboColumn.DisplayStyleForCurrentCellOnly);
        var expectedRowHeight = JoydexTheme.ScaleLogical(JoydexTheme.GridRowHeight, grid.DeviceDpi);
        var expectedHeaderHeight = JoydexTheme.ScaleLogical(JoydexTheme.GridHeaderHeight, grid.DeviceDpi);
        Assert.Equal(expectedRowHeight, grid.RowTemplate.Height);
        Assert.Equal(expectedRowHeight, grid.Rows[0].Height);
        Assert.Equal(expectedHeaderHeight, grid.ColumnHeadersHeight);
        Assert.Equal(
            new Padding(
                JoydexTheme.ScaleLogical(6, grid.DeviceDpi),
                JoydexTheme.ScaleLogical(2, grid.DeviceDpi),
                JoydexTheme.ScaleLogical(6, grid.DeviceDpi),
                JoydexTheme.ScaleLogical(3, grid.DeviceDpi)),
            grid.DefaultCellStyle.Padding);
    }

    [Fact]
    public void RoundedFillRestoresTheSharedGraphicsState()
    {
        using var bitmap = new Bitmap(40, 40);
        using var graphics = Graphics.FromImage(bitmap);
        using var brush = new SolidBrush(Color.Blue);
        graphics.SmoothingMode = SmoothingMode.None;

        ThemeDrawing.FillRoundedRectangle(graphics, brush, new Rectangle(2, 2, 32, 24), 8);

        Assert.Equal(SmoothingMode.None, graphics.SmoothingMode);
    }

    [Fact]
    public void BorderedTextBoxLeavesEnoughVerticalRoomForItsEditor()
    {
        using var toolbar = new FlowLayoutPanel { AutoSize = true };
        using var input = new BorderedTextBox();
        toolbar.Controls.Add(input);
        toolbar.CreateControl();
        toolbar.PerformLayout();
        input.CreateControl();

        Assert.True(input.AutoSize);
        Assert.True(input.Height >= input.GetPreferredSize(Size.Empty).Height);
        Assert.True(input.Height > JoydexTheme.StandardControlHeight);
        Assert.True(input.Editor.Multiline);
        Assert.False(input.Editor.AcceptsReturn);
        Assert.False(input.Editor.WordWrap);
        Assert.True(
            input.Editor.ClientSize.Height >= input.Editor.Font.Height + 4,
            $"The editor has {input.Editor.ClientSize.Height}px of inner height for a {input.Editor.Font.Height}px font.");
    }

    [Fact]
    public void RowSeparatorStaysContinuousAfterAHandledCellChangesGraphicsState()
    {
        using var grid = new ModernDataGridView
        {
            AllowUserToAddRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ColumnHeadersVisible = false,
            RowHeadersVisible = false,
            ScrollBars = ScrollBars.None,
            Size = new Size(200, 60),
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = 100 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Width = 100 });
        grid.Rows.Add("rounded", "plain");
        grid.CellPainting += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex != 0 || eventArgs.ColumnIndex != 0 || eventArgs.Graphics is not { } graphics)
            {
                return;
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = new SolidBrush(eventArgs.CellStyle?.BackColor ?? grid.DefaultCellStyle.BackColor);
            graphics.FillRectangle(background, eventArgs.CellBounds);
            eventArgs.Handled = true;
        };
        grid.CreateControl();
        grid.CurrentCell = null;
        grid.ClearSelection();
        using var bitmap = new Bitmap(grid.Width, grid.Height);

        grid.DrawToBitmap(bitmap, grid.ClientRectangle);

        var rowBounds = grid.GetRowDisplayRectangle(0, cutOverflow: true);
        var separatorY = rowBounds.Bottom - 1;
        // The selected-row accent and focus perimeter can occupy the outermost client pixels.
        for (var x = rowBounds.Left + 2; x < rowBounds.Right - 1; x++)
        {
            var actual = bitmap.GetPixel(x, separatorY).ToArgb();
            Assert.True(
                grid.GridColor.ToArgb() == actual,
                $"Separator pixel at x={x}, y={separatorY} was {actual}, expected {grid.GridColor.ToArgb()}.");
        }
    }

    [Fact]
    public void NavButtonPreferredWidthFitsItsFullLabel()
    {
        using var button = new NavButton
        {
            AutoSize = true,
            Selected = true,
            Text = "Current state",
        };
        var preferred = button.GetPreferredSize(Size.Empty);
        var text = TextRenderer.MeasureText(
            button.Text,
            JoydexTheme.UiSemiboldFont,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        Assert.True(preferred.Width - 44 >= text.Width);
    }

    [Fact]
    public void PrimaryButtonPreferredWidthUsesItsPaintedSemiboldFont()
    {
        using var button = new RoundedButton
        {
            Padding = new Padding(12, 6, 12, 6),
            Text = "+ Add binding",
            Variant = ButtonVariant.Primary,
        };
        var preferred = button.GetPreferredSize(Size.Empty);
        var paintedText = TextRenderer.MeasureText(
            button.Text,
            JoydexTheme.UiSemiboldFont,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        Assert.True(
            preferred.Width >= paintedText.Width + button.Padding.Horizontal + JoydexTheme.ScaleLogical(4, button.DeviceDpi));
    }

    [Fact]
    public void ComboCellEntersEditModeFromOneMouseClick()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                using var host = new Form
                {
                    Bounds = new Rectangle(-32000, -32000, 400, 240),
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                };
                using var grid = new ModernDataGridView
                {
                    AllowUserToAddRows = false,
                    Dock = DockStyle.Fill,
                };
                var choices = new DataGridViewComboBoxColumn { Name = "Choice" };
                choices.Items.AddRange("One", "Two");
                grid.Columns.Add(choices);
                grid.Rows.Add("One");
                host.Controls.Add(grid);
                host.Shown += (_, _) => host.BeginInvoke(() =>
                {
                    var click = new DataGridViewCellMouseEventArgs(
                        columnIndex: 0,
                        rowIndex: 0,
                        localX: 8,
                        localY: 8,
                        new MouseEventArgs(MouseButtons.Left, clicks: 1, x: 8, y: 8, delta: 0));
                    typeof(ModernDataGridView)
                        .GetMethod("OnCellMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(grid, [click]);

                    Assert.Same(grid.Rows[0].Cells[0], grid.CurrentCell);
                    Assert.True(grid.IsCurrentCellInEditMode);
                    var editor = Assert.IsType<DataGridViewComboBoxEditingControl>(grid.EditingControl);
                    Assert.True(editor.DroppedDown);
                    editor.DroppedDown = false;
                    editor.SelectedIndex = 1;
                    grid.NotifyCurrentCellDirty(dirty: true);
                    typeof(ModernDataGridView)
                        .GetMethod("OnComboSelectionChangeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(grid, [editor, EventArgs.Empty]);
                    Assert.Equal("Two", grid.Rows[0].Cells[0].Value);
                    Assert.False(grid.IsCurrentCellInEditMode);
                    host.Close();
                });
                Application.Run(host);
            }
            catch (Exception exception)
            {
                failure = exception is TargetInvocationException { InnerException: not null } target
                    ? target.InnerException
                    : exception;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)), "The one-click grid scenario did not complete.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Theory]
    [InlineData(Keys.Control | Keys.Tab, 0, 2, 1)]
    [InlineData(Keys.Control | Keys.Tab, 1, 2, 0)]
    [InlineData(Keys.Control | Keys.Shift | Keys.Tab, 0, 2, 1)]
    [InlineData(Keys.Control | Keys.Shift | Keys.Tab, 1, 2, 0)]
    [InlineData(Keys.Control | Keys.Tab, 1, 3, 2)]
    [InlineData(Keys.Control | Keys.Shift | Keys.Tab, 1, 3, 0)]
    public void PromptPickerEditorCyclesPagesWithControlTab(
        Keys shortcut,
        int selected,
        int pageCount,
        int expected)
    {
        Assert.Equal(
            expected,
            PromptPickerEditorForm.NavigationPageForShortcut(selected, pageCount, shortcut));
    }

    [Theory]
    [InlineData(Keys.Tab)]
    [InlineData(Keys.Alt | Keys.Tab)]
    [InlineData(Keys.Control | Keys.Alt | Keys.Tab)]
    public void PromptPickerEditorLeavesOtherTabShortcutsAlone(Keys shortcut)
    {
        Assert.Null(PromptPickerEditorForm.NavigationPageForShortcut(0, 2, shortcut));
    }

    [Theory]
    [InlineData(100, 96, 144, 150)]
    [InlineData(150, 144, 96, 100)]
    [InlineData(1, 96, 120, 1)]
    public void DpiScalingConvertsPhysicalMeasurements(int value, int sourceDpi, int targetDpi, int expected)
    {
        Assert.Equal(expected, DpiUtilities.ScaleBetween(value, sourceDpi, targetDpi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorePaletteColorsAreOpaque(bool dark)
    {
        using var theme = JoydexTheme.OverrideDarkMode(dark);

        Assert.All(
            new[]
            {
                JoydexTheme.WindowBg,
                JoydexTheme.Surface,
                JoydexTheme.Border,
                JoydexTheme.Text,
                JoydexTheme.Accent,
                JoydexTheme.AccentTint,
            },
            color => Assert.Equal(255, color.A));
    }
}
