using Joydex.App;
using Joydex.Core.Config;

namespace Joydex.Tests;

public sealed class ButtonMapRenderingTests
{
    private const float PreviewScale = 0.4F;

    private static readonly IReadOnlyDictionary<int, RectangleF> ExpectedRegions =
        new Dictionary<int, RectangleF>
        {
            [13] = new(248, 327, 567, 59),
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
        Assert.NotEqual(
            Color.White.ToArgb(),
            PixelAtSource(preview, sourceX, sourceY).ToArgb());

    private static void AssertOutside(Bitmap preview, int button, float sourceX, float sourceY) =>
        Assert.True(
            PixelAtSource(preview, sourceX, sourceY).ToArgb() == Color.White.ToArgb(),
            $"Button {button} overlay escaped its expected native region near ({sourceX}, {sourceY}).");

    private static Color PixelAtSource(Bitmap preview, float sourceX, float sourceY) => preview.GetPixel(
        (int)MathF.Round(sourceX * PreviewScale),
        (int)MathF.Round(sourceY * PreviewScale));
}
