using Joydex.App;
using Joydex.Core.Config;

namespace Joydex.Tests;

public sealed class ButtonMapRenderingTests
{
    private const float PreviewScale = 0.4F;

    private static readonly IReadOnlyDictionary<int, RectangleF> ExpectedRegions =
        new Dictionary<int, RectangleF>
        {
            [4] = new(1638, 598, 530, 59),
            [13] = new(248, 327, 567, 59),
            [21] = new(1812, 1350, 499, 59),
            [32] = new(727, 1326, 346, 58),
            [33] = new(749, 1411, 362, 59),
            [34] = new(2028, 1990, 383, 59),
            [35] = new(1978, 2210, 382, 59),
            [56] = new(2606, 687, 208, 118),
            [79] = new(3053, 2188, 209, 110),
            [53] = new(1366, 2327, 388, 59),
            [49] = new(160, 2285, 375, 65),
        };

    [Fact]
    public void CanonicalBitmapCoordinatesMatchTheOwnedCm3Template()
    {
        Assert.Equal(new Size(3300, 2550), ButtonMapCanvas.CanonicalTemplateSize);
    }

    [Fact]
    public void EveryPrintedCm3ButtonHasAnAuditedRegion()
    {
        var regions = ButtonMapCanvas.Cm3ButtonRegionsForTesting;

        Assert.Equal(Enumerable.Range(1, 79), regions.Keys.OrderBy(button => button));
        Assert.Equal(regions.Count, regions.Values.Distinct().Count());

        foreach (var (button, region) in regions)
        {
            Assert.True(region.Width > 0, $"Button {button} has no usable width.");
            Assert.True(region.Height > 0, $"Button {button} has no usable height.");
            Assert.True(region.Left >= 0 && region.Top >= 0, $"Button {button} starts outside the template.");
            Assert.True(
                region.Right <= ButtonMapCanvas.CanonicalTemplateSize.Width
                && region.Bottom <= ButtonMapCanvas.CanonicalTemplateSize.Height,
                $"Button {button} extends outside the template.");
        }
    }

    [Fact]
    public void LoaderRejectsMismatchedBitmapsBeforeTheyCanMisalignLabels()
    {
        var root = Path.Combine(Path.GetTempPath(), $"virpil-button-map-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var wrongPath = Path.Combine(root, "wrong.png");
        var correctPath = Path.Combine(root, "correct.png");

        try
        {
            using (var wrong = new Bitmap(3450, 2560))
            {
                wrong.Save(wrongPath);
            }
            using (var correct = new Bitmap(3300, 2550))
            {
                correct.Save(correctPath);
            }

            var logs = new List<string>();
            using var loaded = ButtonMapCanvas.LoadTemplateBitmap([wrongPath, correctPath], logs.Add);

            Assert.NotNull(loaded);
            Assert.Equal(new Size(3300, 2550), loaded.Size);
            Assert.Contains(logs, message => message.Contains("mismatched bitmap was skipped", StringComparison.Ordinal));
            Assert.Contains(logs, message => message.Contains(correctPath, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LocalPackagedBitmapIsPresentAndCanonicalWhenIncluded()
    {
        var packagedPath = Path.Combine(AppContext.BaseDirectory, "Assets", "cm3-button-map.png");
        if (!File.Exists(packagedPath))
        {
            return;
        }

        using var loaded = ButtonMapCanvas.LoadTemplateBitmap([packagedPath]);

        Assert.NotNull(loaded);
        Assert.Equal(new Size(3300, 2550), loaded.Size);
    }

    [Fact]
    public void AlphaWarbrdTemplateIsPackagedAtItsAuditedSize()
    {
        var packagedPath = Path.Combine(AppContext.BaseDirectory, "Assets", "alpha-warbrd-button-map.png");

        Assert.True(File.Exists(packagedPath), $"Missing packaged Alpha/WarBRD map at {packagedPath}.");
        using var loaded = new Bitmap(packagedPath);
        Assert.Equal(new Size(1180, 748), loaded.Size);
    }

    [Fact]
    public void BitmapAndRepresentativeLabelsUseTheSameNativePixelCoordinates()
    {
        var config = new CompanionConfig
        {
            Bindings = ExpectedRegions.Keys.Select(button => new ButtonBinding
            {
                Name = $"Button {button}",
                Bank = CompanionConfig.AlwaysBank,
                Button = button,
                Action = "reject",
            }).ToList(),
        };
        using var template = new Bitmap(3300, 2550);
        using (var graphics = Graphics.FromImage(template))
        {
            graphics.Clear(Color.White);
        }

        using var canvas = ButtonMapCanvas.CreateWithTemplateForTesting(config, template);
        using var preview = canvas.RenderPreview(new Size(1320, 1020));

        foreach (var (button, region) in ExpectedRegions)
        {
            AssertInside(preview, button, region.Left + 20, region.Top + region.Height / 2);
            AssertInside(preview, button, region.Right - 20, region.Top + region.Height / 2);
            AssertInside(preview, button, region.Left + region.Width / 2, region.Top + 20);
            AssertInside(preview, button, region.Left + region.Width / 2, region.Bottom - 20);

            AssertOutside(preview, button, region.Left - 20, region.Top + region.Height / 2);
            AssertOutside(preview, button, region.Right + 20, region.Top + region.Height / 2);
            AssertOutside(preview, button, region.Left + region.Width / 2, region.Top - 20);
            AssertOutside(preview, button, region.Left + region.Width / 2, region.Bottom + 20);
        }
    }

    private static void AssertInside(Bitmap preview, int button, float sourceX, float sourceY) =>
        Assert.True(
            HasNonWhitePixelNear(preview, sourceX, sourceY),
            $"Button {button} overlay did not fill its expected native region near ({sourceX}, {sourceY}).");

    private static void AssertOutside(Bitmap preview, int button, float sourceX, float sourceY) =>
        Assert.True(
            PixelAtSource(preview, sourceX, sourceY).ToArgb() == Color.White.ToArgb(),
            $"Button {button} overlay escaped its expected native region near ({sourceX}, {sourceY}).");

    private static Color PixelAtSource(Bitmap preview, float sourceX, float sourceY) => preview.GetPixel(
        (int)MathF.Round(sourceX * PreviewScale),
        (int)MathF.Round(sourceY * PreviewScale));

    private static bool HasNonWhitePixelNear(Bitmap preview, float sourceX, float sourceY)
    {
        var centerX = (int)MathF.Round(sourceX * PreviewScale);
        var centerY = (int)MathF.Round(sourceY * PreviewScale);
        for (var y = Math.Max(0, centerY - 2); y <= Math.Min(preview.Height - 1, centerY + 2); y++)
        {
            for (var x = Math.Max(0, centerX - 2); x <= Math.Min(preview.Width - 1, centerX + 2); x++)
            {
                if (preview.GetPixel(x, y).ToArgb() != Color.White.ToArgb())
                {
                    return true;
                }
            }
        }

        return false;
    }
}
