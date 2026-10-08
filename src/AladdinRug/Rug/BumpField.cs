using System;
using System.Collections.Generic;
using System.Linq;

namespace AladdinRug
{
    /// <summary>A round lump under the rug, in the picture's own pixels.</summary>
    internal struct Blob { public float X, Y, Rx, Ry, Height; }

    /// <summary>Turns the lumps under the rug into light and shade on its surface (light from the upper left).</summary>
    internal static class BumpField
    {
        private const float Lx = -0.45f, Ly = -0.55f, Lz = 0.70f;

        /// <summary>The lump that <paramref name="count"/> icons piled into the cell at (cellX, cellY) make; positions are in the picture's own pixels.</summary>
        public static Blob BlobAt(double cellX, double cellY, int cellW, int cellH, int count)
        {
            int n = Math.Min(count, 7);
            return new Blob
            {
                X = (float)(cellX + cellW * 0.5), Y = (float)(cellY + cellH * 0.40),
                Rx = cellW * (0.30f + 0.015f * Math.Min(count, 6)), Ry = cellH * (0.27f + 0.015f * Math.Min(count, 6)),
                Height = 6f + 4.5f * n,
            };
        }

        /// <summary>The lumps for a monitor: one per occupied icon cell, taller where more icons are piled up. Positions are monitor-local pixels.</summary>
        public static List<Blob> BlobsFor(DeskGeometry geo)
        {
            int cw = SweptStore.CellW, ch = SweptStore.CellH;
            var blobs = new List<Blob>();
            foreach (var g in SweptStore.Items.Where(i => i.Lump && i.X >= geo.X && i.X < geo.X + geo.W && i.Y >= geo.Y && i.Y < geo.Y + geo.H).GroupBy(i => (i.X, i.Y)))
            {
                int n = g.Count();
                blobs.Add(new Blob
                {
                    X = g.Key.X + cw * 0.5f - geo.X, Y = g.Key.Y + ch * 0.40f - geo.Y,
                    Rx = cw * (0.30f + 0.015f * Math.Min(n, 6)), Ry = ch * (0.27f + 0.015f * Math.Min(n, 6)),
                    Height = 6f + 4.5f * Math.Min(n, 7),
                });
            }
            return blobs;
        }

        /// <summary>Shade for every pixel of a w x h picture (0 where flat; positive = lit slope, negative = shaded). Null if there are no lumps.</summary>
        public static float[] Build(List<Blob> blobs, int w, int h)
        {
            if (blobs == null || blobs.Count == 0) return null;
            var height = new float[w * h];
            int bx0 = w, by0 = h, bx1 = 0, by1 = 0;
            foreach (Blob b in blobs)
            {
                int x0 = Math.Max(1, (int)(b.X - b.Rx) - 1), x1 = Math.Min(w - 2, (int)(b.X + b.Rx) + 1);
                int y0 = Math.Max(1, (int)(b.Y - b.Ry) - 1), y1 = Math.Min(h - 2, (int)(b.Y + b.Ry) + 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = MathF.Abs(x - b.X) / b.Rx, dy = MathF.Abs(y - b.Y) / b.Ry;
                        float r = MathF.Cbrt(dx * dx * dx + dy * dy * dy);        // a squarish outline: cloth over boxes and folders, not balls
                        if (r >= 1f) continue;
                        float t = Math.Min(1f, (1f - r) / 0.6f);
                        height[y * w + x] += b.Height * t * t * (3f - 2f * t);     // flat on top, sloping down the sides
                    }
                bx0 = Math.Min(bx0, x0); by0 = Math.Min(by0, y0); bx1 = Math.Max(bx1, x1); by1 = Math.Max(by1, y1);
            }

            float lLen = MathF.Sqrt(Lx * Lx + Ly * Ly + Lz * Lz);
            var shade = new float[w * h];
            for (int y = Math.Max(1, by0 - 1); y <= Math.Min(h - 2, by1 + 1); y++)
                for (int x = Math.Max(1, bx0 - 1); x <= Math.Min(w - 2, bx1 + 1); x++)
                {
                    float gx = (height[y * w + x + 1] - height[y * w + x - 1]) * 0.5f, gy = (height[(y + 1) * w + x] - height[(y - 1) * w + x]) * 0.5f;
                    float nl = MathF.Sqrt(gx * gx + gy * gy + 1f);
                    float dot = (-gx * Lx - gy * Ly + Lz) / (nl * lLen);          // 1 on flat ground
                    float flat = Lz / lLen;
                    shade[y * w + x] = Math.Max(-0.55f, Math.Min(0.45f, (dot - flat) * 2.4f));
                }
            return shade;
        }

        /// <summary>
        /// A see-through picture the size of the flat rug's window that, laid over the rug, shows the lumps (light and dark) on the rug's own pixels.
        /// </summary>
        public static byte[] Overlay(float[] shade, int monitorW, int monitorH, int windowX, int windowY, int w, int h, byte[] rugPixels)
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int mx = windowX + x, my = windowY + y;
                    if (mx < 0 || my < 0 || mx >= monitorW || my >= monitorH) continue;
                    float s = shade[my * monitorW + mx];
                    if (s > -0.01f && s < 0.01f) continue;
                    float rug = rugPixels[(y * w + x) * 4 + 3] / 255f;
                    if (rug < 0.05f) continue;
                    float a = Math.Min(1f, Math.Abs(s) * (s < 0 ? 0.85f : 0.40f)) * rug;       // cloth catches the light softly, but shades clearly
                    byte v = (byte)(s < 0 ? 0 : 255);
                    int i = (y * w + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = (byte)(v * a);       // premultiplied
                    px[i + 3] = (byte)(a * 255f);
                }
            return px;
        }
    }
}
