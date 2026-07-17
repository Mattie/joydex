using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using VirpilCodexPad.Core.Config;

namespace VirpilCodexPad.App;

internal sealed class ButtonMapForm : Form
{
    private readonly string _windowStatePath;
    private readonly ButtonMapCanvas _canvas;

    public ButtonMapForm(CompanionConfig config, string windowStatePath, Action<string>? log = null)
    {
        _windowStatePath = windowStatePath;

        Text = "VIRPIL CM3 Codex Button Map";
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimumSize = new Size(850, 650);
        ShowIcon = false;
        ShowInTaskbar = false;
        TopMost = true;

        _canvas = new ButtonMapCanvas(config, log) { Dock = DockStyle.Fill };
        Controls.Add(_canvas);
        RestoreWindowState();
    }

    protected override bool ShowWithoutActivation => true;

    public void UpdateConfig(CompanionConfig config) => _canvas.UpdateConfig(config);

    public void ShowReference()
    {
        if (!Visible)
        {
            Show();
        }

        BringToFront();
    }

    public void HideReference()
    {
        if (!Visible)
        {
            return;
        }

        SaveWindowState();
        Hide();
    }

    public void SaveWindowState()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        ButtonMapWindowStateStore.Save(
            _windowStatePath,
            new ButtonMapWindowState(
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                WindowState == FormWindowState.Maximized));
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        SaveWindowState();
        if (eventArgs.CloseReason == CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            Hide();
        }

        base.OnFormClosing(eventArgs);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideReference();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private void RestoreWindowState()
    {
        var state = ButtonMapWindowStateStore.Load(_windowStatePath);
        if (state is not null)
        {
            var requested = new Rectangle(state.Left, state.Top, state.Width, state.Height);
            var screen = Screen.AllScreens.FirstOrDefault(candidate =>
                candidate.WorkingArea.IntersectsWith(requested));
            if (screen is not null)
            {
                Bounds = ClampToWorkingArea(requested, screen.WorkingArea);
                if (state.Maximized)
                {
                    WindowState = FormWindowState.Maximized;
                }

                return;
            }
        }

        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = Math.Min(1600, workingArea.Width - 60);
        var height = Math.Min(1150, workingArea.Height - 60);
        Size = new Size(Math.Max(MinimumSize.Width, width), Math.Max(MinimumSize.Height, height));
        Location = new Point(
            Math.Max(workingArea.Left, workingArea.Right - Width - 30),
            workingArea.Top + 30);
    }

    private static Rectangle ClampToWorkingArea(Rectangle requested, Rectangle workingArea)
    {
        var width = Math.Clamp(requested.Width, 850, workingArea.Width);
        var height = Math.Clamp(requested.Height, 650, workingArea.Height);
        var left = Math.Clamp(requested.Left, workingArea.Left, workingArea.Right - width);
        var top = Math.Clamp(requested.Top, workingArea.Top, workingArea.Bottom - height);
        return new Rectangle(left, top, width, height);
    }
}

internal sealed class ButtonMapCanvas : Control
{
    private const int TemplateWidth = 3300;
    private const int TemplateHeight = 2550;
    private const string TemplateAssetPath = "Assets/cm3-button-map.png";
    private static readonly IReadOnlyDictionary<int, RectangleF> ButtonRegions = CreateButtonRegions();
    private readonly Bitmap? _templateBitmap;
    private readonly Size _templateSize;
    private IReadOnlyDictionary<int, string> _labels;

    public ButtonMapCanvas(CompanionConfig config, Action<string>? log = null)
        : this(config, LoadTemplateBitmap(log))
    {
    }

    private ButtonMapCanvas(CompanionConfig config, Bitmap? templateBitmap)
    {
        _labels = BuildLabels(config);
        _templateBitmap = templateBitmap;
        _templateSize = _templateBitmap?.Size ?? new Size(TemplateWidth, TemplateHeight);

        BackColor = Color.White;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    internal static ButtonMapCanvas CreateWithTemplateForTesting(
        CompanionConfig config,
        Bitmap templateBitmap) => new(config, templateBitmap);

    internal static Size CanonicalTemplateSize => new(TemplateWidth, TemplateHeight);

    public void UpdateConfig(CompanionConfig config)
    {
        _labels = BuildLabels(config);
        Invalidate();
    }

    public Bitmap RenderPreview(Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(bitmap);
        Draw(graphics, new Rectangle(Point.Empty, size));
        return bitmap;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        Draw(eventArgs.Graphics, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _templateBitmap?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Draw(Graphics graphics, Rectangle bounds)
    {
        graphics.Clear(BackColor);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // ButtonRegions use the bitmap's native pixels. One layout calculation keeps
        // the background and every overlay in the same coordinate space.
        var layout = FitLayout(bounds, _templateSize);
        DrawTemplate(graphics, layout.Destination);
        DrawLabels(graphics, layout);
    }

    private void DrawTemplate(Graphics graphics, RectangleF destination)
    {
        if (_templateBitmap is not null)
        {
            graphics.DrawImage(_templateBitmap, destination);
            return;
        }

        DrawGeneratedTemplate(graphics, destination);
    }

    private static Bitmap? LoadTemplateBitmap(Action<string>? log)
    {
        var packagedPath = Path.Combine(AppContext.BaseDirectory, TemplateAssetPath);
        var userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VirpilCodexPad",
            "cm3-button-map.png");
        return LoadTemplateBitmap([userPath, packagedPath], log);
    }

    internal static Bitmap? LoadTemplateBitmap(
        IEnumerable<string> candidatePaths,
        Action<string>? log = null)
    {
        foreach (var path in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
                var bitmap = new Bitmap(decoded);
                if (bitmap.Size != CanonicalTemplateSize)
                {
                    var message =
                        $"CM3 button-map bitmap '{path}' is {bitmap.Width}x{bitmap.Height}; "
                        + $"expected {TemplateWidth}x{TemplateHeight}. The mismatched bitmap was skipped.";
                    bitmap.Dispose();
                    Trace.TraceError(message);
                    log?.Invoke(message);
                    continue;
                }

                log?.Invoke($"Loaded the CM3 button-map bitmap from '{path}' ({bitmap.Width}x{bitmap.Height}).");
                return bitmap;
            }
            catch (Exception error)
            {
                Trace.TraceError("CM3 button-map asset could not be loaded from {0}: {1}", path, error.Message);
                log?.Invoke($"Could not load the CM3 button-map bitmap from '{path}': {error.Message}");
            }
        }

        Trace.TraceWarning("CM3 button-map assets were not found; using the generated fallback.");
        log?.Invoke("CM3 button-map bitmap unavailable; using the generated fallback.");
        return null;
    }

    private static void DrawGeneratedTemplate(Graphics graphics, RectangleF destination)
    {
        var state = graphics.Save();
        var scale = destination.Width / TemplateWidth;
        graphics.TranslateTransform(destination.X, destination.Y);
        graphics.ScaleTransform(scale, scale);

        try
        {
            using var background = new SolidBrush(Color.FromArgb(247, 249, 252));
            using var panelFill = new SolidBrush(Color.White);
            using var schematicFill = new SolidBrush(Color.FromArgb(224, 232, 240));
            using var schematicDark = new SolidBrush(Color.FromArgb(74, 91, 108));
            using var accentFill = new SolidBrush(Color.FromArgb(215, 231, 247));
            using var textBrush = new SolidBrush(Color.FromArgb(18, 52, 77));
            using var mutedTextBrush = new SolidBrush(Color.FromArgb(79, 96, 112));
            using var borderPen = new Pen(Color.FromArgb(126, 148, 168), 3F);
            using var accentPen = new Pen(Color.FromArgb(39, 112, 165), 5F);
            using var titleFont = new Font("Segoe UI Semibold", 52F, FontStyle.Bold, GraphicsUnit.Pixel);
            using var subtitleFont = new Font("Segoe UI", 25F, FontStyle.Regular, GraphicsUnit.Pixel);
            using var headingFont = new Font("Segoe UI Semibold", 27F, FontStyle.Bold, GraphicsUnit.Pixel);
            using var badgeFont = new Font("Segoe UI Semibold", 22F, FontStyle.Bold, GraphicsUnit.Pixel);
            using var center = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            graphics.FillRectangle(background, 0, 0, TemplateWidth, TemplateHeight);
            graphics.DrawRectangle(borderPen, 6, 6, TemplateWidth - 12, TemplateHeight - 12);
            graphics.DrawString(
                "CM3 CONTROL MAP",
                titleFont,
                textBrush,
                new RectangleF(900, 35, 1650, 70),
                center);
            graphics.DrawString(
                "Logical DirectInput buttons with live Codex bindings",
                subtitleFont,
                mutedTextBrush,
                new RectangleF(900, 105, 1650, 45),
                center);

            DrawSchematic(graphics, schematicFill, schematicDark, accentFill, accentPen, headingFont, textBrush, center);

            foreach (var (button, region) in ButtonRegions.OrderBy(pair => pair.Key))
            {
                graphics.FillRectangle(panelFill, region);
                graphics.DrawRectangle(borderPen, region.X, region.Y, region.Width, region.Height);
                DrawButtonBadge(graphics, region, button, badgeFont, schematicDark, panelFill, accentPen);
            }

            DrawSectionHeading(graphics, "SLEW", 244, 280, 575, headingFont, textBrush, center);
            DrawSectionHeading(graphics, "GRIP ENCODER", 1844, 298, 550, headingFont, textBrush, center);
            DrawSectionHeading(graphics, "GRIP HAT", 966, 635, 461, headingFont, textBrush, center);
            DrawSectionHeading(graphics, "THROTTLE ENCODER", 1811, 723, 526, headingFont, textBrush, center);
            DrawSectionHeading(graphics, "LEFT GRIP HAT", 311, 484, 539, headingFont, textBrush, center);
            DrawSectionHeading(graphics, "RIGHT GRIP HAT", 2002, 955, 538, headingFont, textBrush, center);
            DrawSectionHeading(graphics, "AUX HAT", 439, 933, 539, headingFont, textBrush, center);
            DrawSectionHeading(graphics, "BASE HAT", 2027, 1447, 539, headingFont, textBrush, center);
            DrawSectionHeading(graphics, "ENCODER E1", 723, 2150, 396, headingFont, textBrush, center);
            DrawSectionHeading(graphics, "ENCODER E2", 1362, 2150, 396, headingFont, textBrush, center);

            DrawToggleHeading(graphics, "T3", 75, 1401, headingFont, textBrush);
            DrawToggleHeading(graphics, "T4", 75, 1607, headingFont, textBrush);
            DrawToggleHeading(graphics, "T5", 75, 1812, headingFont, textBrush);
            DrawToggleHeading(graphics, "T6", 75, 2017, headingFont, textBrush);
            DrawToggleHeading(graphics, "T7", 75, 2220, headingFont, textBrush);

            DrawBankHeading(graphics, "M1", 234, headingFont, textBrush, center);
            DrawBankHeading(graphics, "M2", 679, headingFont, textBrush, center);
            DrawBankHeading(graphics, "M3", 1124, headingFont, textBrush, center);
            DrawBankHeading(graphics, "M4", 1569, headingFont, textBrush, center);
            DrawBankHeading(graphics, "M5", 2014, headingFont, textBrush, center);

            graphics.DrawString(
                "Generated fallback - CM3 bitmap asset unavailable",
                subtitleFont,
                mutedTextBrush,
                new RectangleF(900, 2480, 1650, 45),
                center);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawSchematic(
        Graphics graphics,
        Brush lightFill,
        Brush darkFill,
        Brush accentFill,
        Pen accentPen,
        Font headingFont,
        Brush textBrush,
        StringFormat center)
    {
        graphics.FillRectangle(lightFill, 1030, 1510, 840, 430);
        graphics.DrawRectangle(accentPen, 1030, 1510, 840, 430);
        graphics.FillRectangle(darkFill, 1135, 830, 250, 680);
        graphics.FillEllipse(darkFill, 1110, 735, 300, 210);
        graphics.FillRectangle(darkFill, 1515, 790, 250, 720);
        graphics.FillEllipse(darkFill, 1490, 695, 300, 210);
        graphics.FillRectangle(accentFill, 1140, 1580, 620, 250);
        graphics.DrawRectangle(accentPen, 1140, 1580, 620, 250);

        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                graphics.FillEllipse(
                    darkFill,
                    1210 + column * 185,
                    1615 + row * 105,
                    72,
                    72);
            }
        }

        graphics.DrawString(
            "THROTTLE",
            headingFont,
            textBrush,
            new RectangleF(1170, 1850, 560, 55),
            center);
    }

    private static void DrawSectionHeading(
        Graphics graphics,
        string text,
        float left,
        float top,
        float width,
        Font font,
        Brush brush,
        StringFormat center) =>
        graphics.DrawString(text, font, brush, new RectangleF(left, top, width, 38), center);

    private static void DrawToggleHeading(
        Graphics graphics,
        string text,
        float left,
        float top,
        Font font,
        Brush brush) =>
        graphics.DrawString(text, font, brush, new PointF(left, top + 17));

    private static void DrawBankHeading(
        Graphics graphics,
        string text,
        float top,
        Font font,
        Brush brush,
        StringFormat center) =>
        graphics.DrawString(text, font, brush, new RectangleF(2600, top - 52, 668, 42), center);

    private void DrawLabels(Graphics graphics, ButtonMapLayout layout)
    {
        var destination = layout.Destination;
        var scale = layout.Scale;
        var fontSize = Math.Clamp(25F * scale, 7.5F, 15F);
        using var font = new Font("Segoe UI Semibold", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var badgeFont = new Font(
            "Segoe UI Semibold",
            Math.Clamp(18F * scale, 6F, 11F),
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(18, 52, 77));
        using var fillBrush = new SolidBrush(Color.FromArgb(224, 235, 247, 255));
        using var badgeFillBrush = new SolidBrush(Color.FromArgb(74, 91, 108));
        using var badgeTextBrush = new SolidBrush(Color.White);
        using var borderPen = new Pen(Color.FromArgb(110, 39, 112, 165), Math.Max(1F, scale * 2F));
        using var badgeBorderPen = new Pen(Color.FromArgb(39, 112, 165), Math.Max(1F, scale * 1.5F));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit,
        };

        var unplaced = new List<string>();
        foreach (var (button, label) in _labels.OrderBy(pair => pair.Key))
        {
            if (!ButtonRegions.TryGetValue(button, out var sourceRegion))
            {
                unplaced.Add($"{button}: {label}");
                continue;
            }

            var region = Transform(sourceRegion, destination, scale);
            var badgeHeight = Math.Clamp(region.Height * 0.45F, 14F, 38F);
            var labelInset = Math.Min(region.Width * 0.35F, badgeHeight * 1.55F + 6F);
            var labelRegion = new RectangleF(
                region.X + labelInset,
                region.Y,
                region.Width - labelInset - 3F,
                region.Height);
            graphics.FillRectangle(fillBrush, region);
            graphics.DrawRectangle(borderPen, region.X, region.Y, region.Width, region.Height);
            graphics.DrawString(label, font, textBrush, labelRegion, format);
            DrawButtonBadge(
                graphics,
                region,
                button,
                badgeFont,
                badgeFillBrush,
                badgeTextBrush,
                badgeBorderPen);
        }

        DrawFallbackLegend(graphics, destination, unplaced);
    }

    private static void DrawButtonBadge(
        Graphics graphics,
        RectangleF region,
        int button,
        Font font,
        Brush fill,
        Brush text,
        Pen border)
    {
        var badgeHeight = Math.Clamp(region.Height * 0.45F, 14F, 38F);
        var badgeWidth = badgeHeight * 1.35F;
        var badge = new RectangleF(
            region.Left + Math.Max(3F, region.Height * 0.08F),
            region.Top + Math.Max(3F, region.Height * 0.08F),
            badgeWidth,
            badgeHeight);
        using var center = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        graphics.FillRectangle(fill, badge);
        graphics.DrawRectangle(border, badge.X, badge.Y, badge.Width, badge.Height);
        graphics.DrawString(button.ToString(), font, text, badge, center);
    }

    private static void DrawFallbackLegend(
        Graphics graphics,
        RectangleF destination,
        IReadOnlyList<string> unplaced)
    {
        if (unplaced.Count == 0)
        {
            return;
        }

        var legendWidth = Math.Min(420F, destination.Width * 0.28F);
        var legendHeight = Math.Min(destination.Height * 0.32F, 34F + unplaced.Count * 22F);
        var legend = new RectangleF(
            destination.X + (destination.Width - legendWidth) / 2F,
            destination.Y + 12F,
            legendWidth,
            legendHeight);
        using var fill = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        using var border = new Pen(Color.FromArgb(39, 112, 165), 1.5F);
        using var font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
        using var brush = new SolidBrush(Color.FromArgb(18, 52, 77));
        graphics.FillRectangle(fill, legend);
        graphics.DrawRectangle(border, legend.X, legend.Y, legend.Width, legend.Height);
        graphics.DrawString(
            string.Join(Environment.NewLine, unplaced),
            font,
            brush,
            RectangleF.Inflate(legend, -8F, -6F));
    }

    private static ButtonMapLayout FitLayout(Rectangle bounds, Size imageSize)
    {
        var scale = Math.Min(
            bounds.Width / (float)imageSize.Width,
            bounds.Height / (float)imageSize.Height);
        var width = imageSize.Width * scale;
        var height = imageSize.Height * scale;
        return new ButtonMapLayout(
            new RectangleF(
                bounds.Left + (bounds.Width - width) / 2F,
                bounds.Top + (bounds.Height - height) / 2F,
                width,
                height),
            scale);
    }

    private static RectangleF Transform(RectangleF source, RectangleF destination, float scale) => new(
        destination.X + source.X * scale,
        destination.Y + source.Y * scale,
        source.Width * scale,
        source.Height * scale);

    private readonly record struct ButtonMapLayout(RectangleF Destination, float Scale);

    private static IReadOnlyDictionary<int, string> BuildLabels(CompanionConfig config) =>
        config.Bindings
            .GroupBy(binding => binding.Button)
            .ToDictionary(
                group => group.Key,
                group => string.Join(
                    " / ",
                    group.Select(binding => DisplayAction(binding.Action))
                        .Distinct(StringComparer.OrdinalIgnoreCase)));

    private static string DisplayAction(string action) => action.ToLowerInvariant() switch
    {
        "agent-1" => "Agent 1",
        "agent-2" => "Agent 2",
        "agent-3" => "Agent 3",
        "agent-4" => "Agent 4",
        "agent-5" => "Agent 5",
        "agent-6" => "Agent 6",
        "fast-mode" => "Fast mode",
        "approve" => "Approve",
        "reject" => "Reject",
        "fork-task" => "Fork task",
        "push-to-talk" => "Hold to talk",
        "submit" => "Submit",
        "plan-mode" => "Plan mode",
        "reasoning-up" => "Reasoning +",
        "reasoning-down" => "Reasoning -",
        "scroll-up" => "Scroll up",
        "scroll-down" => "Scroll down",
        "home" => "Home",
        "end" => "End",
        "button-map" => "Button map",
        "new-task" => "New task",
        "previous-task" => "Previous task",
        "next-task" => "Next task",
        "navigate-back" => "Back",
        "navigate-forward" => "Forward",
        "toggle-sidebar" => "Sidebar",
        "open-skills" => "Skills",
        "dictation" => "Dictation",
        "open" => "Open",
        _ => action.Replace('-', ' '),
    };

    private static IReadOnlyDictionary<int, RectangleF> CreateButtonRegions()
    {
        var regions = new Dictionary<int, RectangleF>();

        AddRows(regions, [13], 244, 324, 819, 65);
        AddRows(regions, [3, 2, 1], 1844, 342, 2394, 64.7F);
        AddRows(regions, [7, 6, 5], 966, 679, 1427, 64.7F);
        AddRows(regions, [15, 14], 1811, 767, 2337, 64.5F);
        AddRows(regions, [11, 10, 9, 12, 8], 311, 528, 850, 64.8F);
        AddRows(regions, [20, 17, 18, 19, 16], 2002, 999, 2540, 64.6F);
        AddRows(regions, [25, 26, 23, 24, 22], 439, 977, 978, 64.8F);
        AddRows(regions, [30, 31, 28, 29, 27], 2027, 1491, 2566, 64.8F);

        regions[36] = new RectangleF(160, 1401, 376, 65);
        regions[37] = new RectangleF(160, 1607, 375, 64);
        regions[44] = new RectangleF(160, 1812, 375, 64);
        regions[45] = new RectangleF(160, 1876, 375, 65);
        regions[46] = new RectangleF(160, 2017, 375, 65);
        regions[47] = new RectangleF(160, 2082, 375, 64);
        regions[48] = new RectangleF(160, 2220, 375, 65);
        regions[49] = new RectangleF(160, 2285, 375, 65);

        AddRows(regions, [52, 51, 50], 723, 2194, 1119, 65);
        AddRows(regions, [54, 55, 53], 1362, 2194, 1758, 65);

        AddBank(regions, 38, 234, 400, 558);
        AddBank(regions, 56, 679, 845, 1003);
        AddBank(regions, 62, 1124, 1290, 1448);
        AddBank(regions, 68, 1569, 1735, 1893);
        AddBank(regions, 74, 2014, 2180, 2338);

        return regions;
    }

    private static void AddRows(
        IDictionary<int, RectangleF> regions,
        IReadOnlyList<int> buttons,
        float left,
        float top,
        float right,
        float rowHeight)
    {
        for (var index = 0; index < buttons.Count; index++)
        {
            regions[buttons[index]] = new RectangleF(
                left + 4,
                top + index * rowHeight + 3,
                right - left - 8,
                rowHeight - 6);
        }
    }

    private static void AddBank(
        IDictionary<int, RectangleF> regions,
        int firstButton,
        float top,
        float middle,
        float bottom)
    {
        float[] columns = [2600, 2820, 3047, 3268];
        for (var column = 0; column < 3; column++)
        {
            regions[firstButton + column] = new RectangleF(
                columns[column] + 6,
                top + 8,
                columns[column + 1] - columns[column] - 12,
                middle - top - 48);
            regions[firstButton + column + 3] = new RectangleF(
                columns[column] + 6,
                middle + 8,
                columns[column + 1] - columns[column] - 12,
                bottom - middle - 48);
        }
    }
}
