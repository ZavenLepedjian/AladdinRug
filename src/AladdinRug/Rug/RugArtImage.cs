using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AladdinRug
{
    internal static partial class RugArt
    {
        /// <summary>Check the selected built-in art before deleting the image or replacing any windows.</summary>
        internal static bool CanRestoreBuiltin(int roomWidth, int roomHeight, double scale, double layoutScale, RugStyle style, out string error)
        {
            try
            {
                StyleSpec spec = RugStyles.Get(style);
                // Match RugWindow's built-in dimensions, including Doormat's smaller size.
                int width = Math.Max(400, (int)(roomWidth * DefaultWidthFraction * spec.WidthFactor));
                int height = Math.Max(300, (int)(roomHeight * DefaultHeightFraction * spec.HeightFactor));
                _ = RenderAll(width, height, scale, layoutScale, style);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException
                || ex is IndexOutOfRangeException || ex is NotSupportedException)
            {
                error = "The " + style + " rug could not be drawn at this monitor's size. Your image has been kept. "
                    + "Choose another cloth feel, then clear the image again.";
                return false;
            }
        }

        /// <summary>An image is fitted uniformly without a backing, fringe, weave or character.</summary>
        public static Images RenderImage(int pw, int ph, double s, double layoutScale, BitmapSource image, bool foldShading)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var brush = new ImageBrush(image) { Stretch = Stretch.Uniform };
                brush.Freeze();
                dc.DrawRectangle(brush, null, BodyRect(pw, ph, s, image: true));
            }
            var target = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var face = new byte[pw * ph * 4];
            target.CopyPixels(face, pw * 4, 0);
            // Reversed triangles mirror the same pixels on the back. No beige underside.
            return new Images { Front = Wrap(face, pw, ph, 96 * layoutScale), FacePx = face, BackPx = (byte[])face.Clone(),
                TexW = pw, TexH = ph, Roll = ImageRoll(face, pw, ph, s, foldShading) };
        }

        private static BitmapSource ImageRoll(byte[] face, int width, int height, double scale, bool shade)
        {
            Rect body = BodyRect(width, height, scale, image: true);
            const int rollWidth = 128;
            int rollHeight = Math.Max(1, (int)body.Height);
            var pixels = new byte[rollWidth * rollHeight * 4];
            for (int y = 0; y < rollHeight; y++)
            {
                int sy = Math.Clamp((int)(body.Top + (y + 0.5) * body.Height / rollHeight), 0, height - 1);
                for (int x = 0; x < rollWidth; x++)
                {
                    double u = 0.5 + Math.Asin(2 * (x + 0.5) / rollWidth - 1) / Math.PI;
                    int sx = Math.Clamp((int)(body.Left + u * body.Width), 0, width - 1);
                    int dst = (y * rollWidth + x) * 4;
                    Array.Copy(face, (sy * width + sx) * 4, pixels, dst, 4);
                    if (shade)
                    {
                        double normal = 2 * (x + 0.5) / rollWidth - 1;
                        double light = 0.55 + 0.45 * Math.Sqrt(1 - normal * normal);
                        for (int channel = 0; channel < 3; channel++) pixels[dst + channel] = (byte)(pixels[dst + channel] * light);
                    }
                }
            }
            return Wrap(pixels, rollWidth, rollHeight, 96);
        }
    }
}
