using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Joydex.App;

internal static class AppIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using var background = new SolidBrush(Color.FromArgb(18, 28, 42));
            using var border = new Pen(Color.FromArgb(56, 189, 248), 2F);
            using var basePen = new Pen(Color.FromArgb(56, 189, 248), 3F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            using var lever = new Pen(Color.White, 4F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            using var knob = new SolidBrush(Color.FromArgb(34, 211, 238));

            graphics.FillEllipse(background, 1, 1, 30, 30);
            graphics.DrawEllipse(border, 2, 2, 28, 28);
            graphics.DrawLine(basePen, 8, 24, 24, 24);
            graphics.DrawLine(lever, 13, 23, 20, 10);
            graphics.FillEllipse(knob, 16, 6, 8, 8);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
