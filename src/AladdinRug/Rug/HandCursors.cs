using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace AladdinRug
{
    /// <summary>The mouse pointer as a human hand: open while hovering over the rug, a grip while holding it.</summary>
    internal static class HandCursors
    {
        private const int Size = 40;
        private static Cursor _open, _closed;

        public static Cursor Open => _open ?? (_open = Make(fist: false));
        public static Cursor Closed => _closed ?? (_closed = Make(fist: true));

        private static Cursor Make(bool fist)
        {
            try
            {
                using (var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        if (fist) DrawFist(g); else DrawOpenHand(g);
                    }

                    IntPtr icon = bmp.GetHicon();
                    if (!GetIconInfo(icon, out ICONINFO info)) { DestroyIcon(icon); return Fallback(fist); }
                    info.fIcon = false;
                    info.xHotspot = Size / 2;
                    info.yHotspot = fist ? 24 : 22;
                    IntPtr cursor = CreateIconIndirect(ref info);
                    DestroyIcon(icon);
                    DeleteObject(info.hbmMask);
                    DeleteObject(info.hbmColor);
                    return cursor == IntPtr.Zero ? Fallback(fist) : CursorInteropHelper.Create(new CursorHandle(cursor));
                }
            }
            catch (Exception) { return Fallback(fist); }
        }

        private static Cursor Fallback(bool fist) => fist ? Cursors.SizeAll : Cursors.Hand;

        private static readonly Color Skin = Color.FromArgb(255, 244, 205, 168);
        private static readonly Color Ink = Color.FromArgb(255, 70, 44, 30);

        // Palm and four fingers, thumb out to the side.
        private static void DrawOpenHand(Graphics g)
        {
            var parts = new[]
            {
                Capsule(13.2f, 22, 13.2f, 11, 4.8f),     // little finger side first (left), index to the right is mirrored below
                Capsule(18.0f, 22, 18.0f, 6, 4.8f),
                Capsule(22.8f, 22, 22.8f, 4, 4.8f),
                Capsule(27.6f, 22, 27.6f, 8, 4.8f),
                Capsule(10.5f, 30, 5.0f, 21, 5.2f),      // thumb
                RoundRect(10.5f, 19, 20, 17, 6.5f),      // palm
            };
            Paint(g, parts);
            using (var pen = new Pen(Ink, 1.1f))                       // creases between the fingers
                foreach (float x in new[] { 15.6f, 20.4f, 25.2f })
                    g.DrawLine(pen, x, 14, x, 22);
        }

        // Closed fingers curled over the palm, thumb across them.
        private static void DrawFist(Graphics g)
        {
            var parts = new[]
            {
                RoundRect(9, 17, 23, 18, 7f),
                Capsule(13f, 21, 13f, 18, 5.4f), Capsule(18f, 21, 18f, 16.5f, 5.4f),
                Capsule(23f, 21, 23f, 16.5f, 5.4f), Capsule(28f, 21, 28f, 18, 5.4f),
                Capsule(12.5f, 29, 24f, 28, 5.2f),                     // thumb folded across
            };
            Paint(g, parts);
            using (var pen = new Pen(Ink, 1.1f))
            {
                foreach (float x in new[] { 15.5f, 20.5f, 25.5f }) g.DrawLine(pen, x, 18f, x, 24f);
                g.DrawLine(pen, 11f, 24.5f, 31f, 24.5f);
            }
        }

        // Outline every piece first, then fill them all, so the hand has one clean outline and no seams inside.
        private static void Paint(Graphics g, GraphicsPath[] parts)
        {
            using (var ink = new Pen(Ink, 2.4f) { LineJoin = LineJoin.Round })
                foreach (GraphicsPath p in parts) g.DrawPath(ink, p);
            using (var skin = new SolidBrush(Skin))
                foreach (GraphicsPath p in parts) g.FillPath(skin, p);
        }

        private static GraphicsPath Capsule(float x1, float y1, float x2, float y2, float width)
        {
            var path = new GraphicsPath();
            float dx = x2 - x1, dy = y2 - y1, len = (float)Math.Sqrt(dx * dx + dy * dy);
            float angle = (float)(Math.Atan2(dy, dx) * 180 / Math.PI);
            float r = width / 2;
            path.AddArc(x2 - r, y2 - r, width, width, angle - 90, 180);
            path.AddArc(x1 - r, y1 - r, width, width, angle + 90, 180);
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            float d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot, yHotspot;
            public IntPtr hbmMask, hbmColor;
        }

        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr cursor);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

        private sealed class CursorHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public CursorHandle(IntPtr cursor) : base(true) { SetHandle(cursor); }
            protected override bool ReleaseHandle() => DestroyCursor(handle);
        }
    }
}
