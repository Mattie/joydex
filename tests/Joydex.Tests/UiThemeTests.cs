using Joydex.App;
using System.Drawing.Drawing2D;

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
