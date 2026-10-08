using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AladdinRug
{
    /// <summary>
    /// Paints the rug: a Persian-style design (borders, trellis field, medallion, corner spandrels, knotted
    /// fringe) drawn as vectors, then run through a pixel pass that adds woven texture, uneven dye, soft
    /// folds, a few lumps (the "stuff underneath") and a drop shadow.
    /// </summary>
    internal static partial class RugArt
    {
        public const double FringeDip = 24;   // fringe length at each short end
        public const double PadDip = 30;       // transparent margin round the rug, room for its shadow and for lifting it
        public const double DefaultWidthFraction = 0.40, DefaultHeightFraction = 0.46;   // default rug size, as a share of the monitor

        // Wool dyes.
        internal static readonly Color Crimson = Rgb(0x7B1C27), CrimsonDeep = Rgb(0x5A111B), CrimsonBright = Rgb(0x92283A);
        internal static readonly Color Navy = Rgb(0x1E2D55), NavyDeep = Rgb(0x131E3A);
        internal static readonly Color Cream = Rgb(0xEADFC0), Ivory = Rgb(0xD9C89C), Gold = Rgb(0xC8993C);
        internal static readonly Color Teal = Rgb(0x2E7176), Rust = Rgb(0xB3532F);

        private static Color Rgb(int v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

        private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        private static Brush Br(Color c, double alpha = 1)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private static Pen Pn(Color c, double width, double alpha = 1, PenLineCap cap = PenLineCap.Flat)
        {
            var p = new Pen(Br(c, alpha), width) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        private static Rect Inset(Rect r, double d) => new Rect(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

        // s sizes the artwork (monitor DPI); layoutScale is how many pixels WPF thinks a DIP is on the window
        // that will show it, so the bitmap lands on the screen 1:1.
        /// <summary>Where the woven part of the rug sits inside its window: inset for the fringe and shadow margin.</summary>
        public static Rect BodyRect(int pw, int ph, double s)
        {
            double padX = (FringeDip + PadDip) * s, padY = PadDip * s;
            return new Rect(padX, padY, pw - 2 * padX, ph - 2 * padY);
        }

        /// <summary>The pictures a rug needs: its face with a shadow under it (for the rug lying flat), and raw pixels of its
        /// face and its dull underside without any shadow (for the cloth, which casts its own).</summary>
        internal sealed class Images
        {
            public BitmapSource Front;
            public byte[] FacePx, BackPx;      // premultiplied BGRA, TexW x TexH
            public int TexW, TexH;
        }

        public static Images RenderAll(int pw, int ph, double s, double layoutScale = 0, RugStyle? style = null)
        {
            StyleSpec spec = RugStyles.Get(style ?? RugStyles.Current);
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
                Draw(dc, pw, ph, s, new Random(11), spec);

            var target = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);

            int stride = pw * 4;
            var px = new byte[stride * ph];
            target.CopyPixels(px, stride, 0);

            if (spec.Style == RugStyle.Shaggy) ShagWeave(px, pw, ph, s, BodyRect(pw, ph, s), spec);
            else Weave(px, pw, ph, s, BodyRect(pw, ph, s), spec);

            byte[] back = MakeUnderside(px, spec);                 // before Aladdin: he sits on the top side only
            if (RugStyles.Aladdin) AddAladdin(px, pw, ph, s, spec);
            byte[] face = (byte[])px.Clone();
            AddShadow(px, pw, ph, s);

            double dpi = 96 * (layoutScale > 0 ? layoutScale : s);
            return new Images { Front = Wrap(px, pw, ph, dpi), FacePx = face, BackPx = back, TexW = pw, TexH = ph };
        }

        private static BitmapSource Wrap(byte[] pixels, int pw, int ph, double dpi)
        {
            var bmp = BitmapSource.Create(pw, ph, dpi, dpi, PixelFormats.Pbgra32, null, pixels, pw * 4);
            bmp.Freeze();
            return bmp;
        }

        // The back of a rug: the same weave seen mirrored, but flat, pale and undyed-looking.
        private static byte[] MakeUnderside(byte[] px, StyleSpec spec)
        {
            Color bc = spec.BackColor;
            var back = new byte[px.Length];
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a == 0) continue;
                float inv = 255f / a;
                float lum = (0.299f * px[i + 2] + 0.587f * px[i + 1] + 0.114f * px[i]) * inv / 255f;
                float k = (0.90f - spec.BackFlat) + spec.BackFlat * lum;   // flatten the contrast of the design
                back[i + 2] = (byte)Math.Min(a, bc.R * k * a / 255f);
                back[i + 1] = (byte)Math.Min(a, bc.G * k * a / 255f);
                back[i] = (byte)Math.Min(a, bc.B * k * a / 255f);
                back[i + 3] = a;
            }
            return back;
        }

        // ---------------------------------------------------------------- vector drawing

        private static void Draw(DrawingContext dc, int pw, int ph, double s, Random rnd, StyleSpec spec)
        {
            switch (spec.Style)
            {
                case RugStyle.Kilim: DrawKilim(dc, pw, ph, s, rnd); break;
                case RugStyle.Shaggy: DrawShag(dc, pw, ph, s, rnd); break;
                case RugStyle.Doormat: DrawDoormat(dc, pw, ph, s, rnd); break;
                default: DrawPersian(dc, pw, ph, s, rnd); break;
            }
        }

        private static void DrawPersian(DrawingContext dc, int pw, int ph, double s, Random rnd)
        {
            Rect body = BodyRect(pw, ph, s);

            DrawFringe(dc, body, s, rnd);

            // Concentric bands, outermost first.
            Fill(dc, NavyDeep, body);                       // selvedge
            Fill(dc, Cream, Inset(body, 5 * s));
            Fill(dc, Navy, Inset(body, 9 * s));             // main border
            Fill(dc, Cream, Inset(body, 55 * s));
            Fill(dc, Gold, Inset(body, 59 * s));
            Fill(dc, Ivory, Inset(body, 62 * s));           // chain band
            Fill(dc, Gold, Inset(body, 76 * s));

            DrawMainBorder(dc, body, s);
            DrawChain(dc, Inset(body, 69 * s), s);

            Rect field = Inset(body, 80 * s);
            var fieldBrush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.78, RadiusY = 0.85,
            };
            fieldBrush.GradientStops.Add(new GradientStop(Lerp(Crimson, CrimsonBright, 0.7), 0));
            fieldBrush.GradientStops.Add(new GradientStop(Crimson, 0.6));
            fieldBrush.GradientStops.Add(new GradientStop(CrimsonDeep, 1));
            fieldBrush.Freeze();
            dc.DrawRectangle(fieldBrush, null, field);

            dc.PushClip(new RectangleGeometry(field));
            DrawTrellis(dc, field, s);
            DrawSpandrels(dc, field, s);
            DrawMedallion(dc, field, s);
            dc.Pop();

            dc.DrawRectangle(null, Pn(Gold, 1.6 * s, 0.9), Inset(field, 6 * s));
            dc.DrawRectangle(null, Pn(Color.FromRgb(0, 0, 0), 1.4 * s, 0.45), body);   // pile edge
        }

        private static void Fill(DrawingContext dc, Color c, Rect r) => dc.DrawRectangle(Br(c), null, r);

        private static void DrawFringe(DrawingContext dc, Rect body, double s, Random rnd)
        {
            double f = FringeDip * s, step = 3.1 * s;
            var tan = Rgb(0xC9B88E);
            var knotFill = Br(Rgb(0xCFBE93));
            var knotPen = Pn(Rgb(0x8E7B52), 0.8 * s);

            for (int side = 0; side < 2; side++)
            {
                double dir = side == 0 ? -1 : 1;
                double edge = side == 0 ? body.Left : body.Right;
                int n = (int)((body.Height - 6 * s) / step);
                double bunchSway = 0;

                for (int j = 0; j < n; j++)
                {
                    double y = body.Top + 3 * s + j * step;
                    if (j % 6 == 0) bunchSway = (rnd.NextDouble() - 0.5) * 9 * s;   // threads in a tassel drift together
                    double len = f * (0.80 + 0.28 * rnd.NextDouble());
                    double sway = bunchSway + (rnd.NextDouble() - 0.5) * 3 * s;

                    var g = new StreamGeometry();
                    using (StreamGeometryContext ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(edge - dir * 2 * s, y), false, false);
                        ctx.QuadraticBezierTo(new Point(edge + dir * len * 0.5, y + sway * 0.25),
                                              new Point(edge + dir * len, y + sway), true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, Pn(Lerp(Cream, tan, rnd.NextDouble() * 0.5), 1.7 * s, 1, PenLineCap.Round), g);
                }

                for (int j = 0; j + 5 < n; j += 6)   // the knot that ties each tassel
                {
                    double cy = body.Top + 3 * s + (j + 2.5) * step;
                    var knot = new Rect(edge + dir * f * 0.18 - 3.5 * s, cy - 8 * s, 7 * s, 16 * s);
                    dc.DrawRoundedRectangle(knotFill, knotPen, knot, 3 * s, 3 * s);
                }
            }
        }

        private static void DrawMainBorder(DrawingContext dc, Rect body, double s)
        {
            Rect cl = Inset(body, 32 * s);   // centre line of the wide border
            double tile = 46 * s;

            foreach (Point c in new[] { cl.TopLeft, cl.TopRight, cl.BottomLeft, cl.BottomRight })
            {
                dc.DrawRectangle(Br(Cream), Pn(NavyDeep, 1.4 * s), new Rect(c.X - tile / 2 + 2 * s, c.Y - tile / 2 + 2 * s, tile - 4 * s, tile - 4 * s));
                Rosette(dc, c, 18 * s, Rust, Navy, Cream);
            }

            BorderRun(dc, cl.TopLeft, cl.TopRight, tile, s);
            BorderRun(dc, cl.BottomLeft, cl.BottomRight, tile, s);
            BorderRun(dc, cl.TopLeft, cl.BottomLeft, tile, s);
            BorderRun(dc, cl.TopRight, cl.BottomRight, tile, s);
        }

        private static void BorderRun(DrawingContext dc, Point a, Point b, double tile, double s)
        {
            Vector d = b - a;
            double len = d.Length;
            d.Normalize();
            var nrm = new Vector(-d.Y, d.X);

            Point start = a + d * tile;
            double usable = len - 2 * tile;
            int n = Math.Max(1, (int)Math.Round(usable / (62 * s)));
            double step = usable / n;

            for (int i = 0; i <= n; i++)
            {
                Point p = start + d * (i * step);
                Diamond(dc, p, d, nrm, 12 * s, 7 * s, Teal, Cream, 1.2 * s);
                dc.DrawEllipse(Br(Gold), null, p + nrm * 15 * s, 2.2 * s, 2.2 * s);
                dc.DrawEllipse(Br(Gold), null, p - nrm * 15 * s, 2.2 * s, 2.2 * s);
            }
            for (int i = 0; i < n; i++)
                Rosette(dc, start + d * ((i + 0.5) * step), 17 * s, Cream, Crimson, Navy);
        }

        private static void DrawChain(DrawingContext dc, Rect centre, double s)
        {
            var corners = new[] { centre.TopLeft, centre.TopRight, centre.BottomRight, centre.BottomLeft };
            for (int k = 0; k < 4; k++)
            {
                Point a = corners[k], b = corners[(k + 1) % 4];
                Vector d = b - a;
                double len = d.Length;
                d.Normalize();
                var nrm = new Vector(-d.Y, d.X);
                int n = Math.Max(1, (int)Math.Round(len / (15 * s)));
                for (int i = 0; i < n; i++)
                    Diamond(dc, a + d * ((i + 0.5) * len / n), d, nrm, 5.5 * s, 5.5 * s, Crimson, NavyDeep, 0.9 * s);
            }
        }

        private static void DrawTrellis(DrawingContext dc, Rect field, double s)
        {
            double h = 44 * s;
            var c = new Point(field.X + field.Width / 2, field.Y + field.Height / 2);
            int ni = (int)(field.Width / 2 / h) + 2, nj = (int)(field.Height / 2 / h) + 2;
            double reach = field.Width + field.Height;
            Pen line = Pn(Gold, 1.3 * s, 0.30);

            for (int m = -(ni + nj); m <= ni + nj; m += 2)
            {
                dc.DrawLine(line, new Point(c.X + m * h - reach, c.Y - reach), new Point(c.X + m * h + reach, c.Y + reach));
                dc.DrawLine(line, new Point(c.X + m * h - reach, c.Y + reach), new Point(c.X + m * h + reach, c.Y - reach));
            }

            Brush goldDot = Br(Gold, 0.55), tealDot = Br(Teal, 0.65);
            for (int i = -ni; i <= ni; i++)
                for (int j = -nj; j <= nj; j++)
                {
                    var p = new Point(c.X + i * h, c.Y + j * h);
                    if (((i + j) & 1) == 0) dc.DrawGeometry(goldDot, null, Star(p, 5.5 * s, 2 * s, 4, -Math.PI / 4));
                    else dc.DrawEllipse(tealDot, null, p, 2.4 * s, 2.4 * s);
                }
        }

        private static void DrawSpandrels(DrawingContext dc, Rect field, double s)
        {
            double a = 0.17 * field.Width, b = 0.40 * field.Height;
            foreach (Point c in new[] { field.TopLeft, field.TopRight, field.BottomLeft, field.BottomRight })
            {
                double sx = c.X < field.X + field.Width / 2 ? 1 : -1, sy = c.Y < field.Y + field.Height / 2 ? 1 : -1;
                dc.DrawGeometry(Br(NavyDeep), Pn(NavyDeep, 5 * s), Lozenge(c, a * 1.03, b * 1.03));
                dc.DrawGeometry(Br(Navy), Pn(Cream, 2.6 * s), Lozenge(c, a, b));
                dc.DrawGeometry(Br(CrimsonDeep), Pn(Gold, 1.6 * s), Lozenge(c, a * 0.80, b * 0.80));
                dc.DrawGeometry(Br(Ivory), Pn(NavyDeep, 1 * s), Lozenge(c, a * 0.56, b * 0.56));
                dc.DrawGeometry(Br(Crimson), Pn(Gold, 1.4 * s), Lozenge(c, a * 0.40, b * 0.40));
                foreach (Point p in Sample(c, a * 0.68, b * 0.68, 4))
                    dc.DrawEllipse(Br(Gold, 0.8), null, p, 2.6 * s, 2.6 * s);
                Rosette(dc, new Point(c.X + sx * a * 0.17, c.Y + sy * b * 0.17), 26 * s, Cream, Rust, Navy);
            }
        }

        private static void DrawMedallion(DrawingContext dc, Rect field, double s)
        {
            var c = new Point(field.X + field.Width / 2, field.Y + field.Height / 2);
            double A = 0.28 * field.Width, B = 0.43 * field.Height;

            // Small pendants off each tip.
            double px = A + 52 * s, py = B + 22 * s;
            foreach (Point p in new[] { new Point(c.X - px, c.Y), new Point(c.X + px, c.Y) })
            {
                dc.DrawGeometry(Br(Navy), Pn(Cream, 2 * s), Lozenge(p, 40 * s, 26 * s));
                dc.DrawGeometry(Br(Gold), null, Lozenge(p, 20 * s, 12 * s));
            }
            foreach (Point p in new[] { new Point(c.X, c.Y - py), new Point(c.X, c.Y + py) })
            {
                dc.DrawGeometry(Br(Navy), Pn(Cream, 2 * s), Lozenge(p, 24 * s, 14 * s));
                dc.DrawGeometry(Br(Gold), null, Lozenge(p, 12 * s, 7 * s));
            }

            // Scalloped rim: lobes around the outline, then the body drawn over their inner halves.
            foreach (Point p in Sample(c, A, B, 9))
                dc.DrawEllipse(Br(Navy), Pn(Cream, 2.6 * s), p, 19 * s, 19 * s);
            dc.DrawGeometry(Br(Navy), Pn(Cream, 3 * s), Lozenge(c, A, B));
            foreach (Point p in Sample(c, A, B, 9))
                dc.DrawEllipse(Br(Gold, 0.9), null, p, 4.5 * s, 4.5 * s);

            dc.DrawGeometry(null, Pn(Gold, 1.6 * s), Lozenge(c, A * 0.92, B * 0.92));
            dc.DrawGeometry(Br(Ivory), Pn(NavyDeep, 1.2 * s), Lozenge(c, A * 0.85, B * 0.85));

            foreach (Point p in Sample(c, A * 0.785, B * 0.785, 7))
                Diamond(dc, p, new Vector(1, 0), new Vector(0, 1), 7 * s, 7 * s, Teal, NavyDeep, 1 * s);

            dc.DrawGeometry(Br(CrimsonBright), Pn(Gold, 2 * s), Lozenge(c, A * 0.72, B * 0.72));
            foreach (Point p in Sample(c, A * 0.63, B * 0.63, 3))
                dc.DrawGeometry(Br(Gold, 0.85), null, Star(p, 9 * s, 4 * s, 8, -Math.PI / 2));

            dc.DrawGeometry(Br(Navy), Pn(Cream, 2 * s), Lozenge(c, A * 0.54, B * 0.54));
            dc.DrawGeometry(Br(Cream), Pn(Gold, 1.6 * s), Star(c, B * 0.40, B * 0.18, 8, -Math.PI / 2));
            dc.DrawGeometry(Br(Rust), Pn(NavyDeep, 1.2 * s), Star(c, B * 0.27, B * 0.115, 8, -Math.PI / 8));
            dc.DrawGeometry(Br(Navy), Pn(Gold, 1.4 * s), Star(c, B * 0.14, B * 0.07, 8, -Math.PI / 2));
            dc.DrawGeometry(Br(Gold), null, Star(c, B * 0.075, B * 0.03, 4, -Math.PI / 2));
        }

        // ---------------------------------------------------------------- shapes

        private static void Rosette(DrawingContext dc, Point c, double r, Color outer, Color mid, Color inner)
        {
            dc.DrawGeometry(Br(outer), Pn(NavyDeep, 0.05 * r), Star(c, r, r * 0.62, 8, -Math.PI / 2));
            dc.DrawEllipse(Br(mid), null, c, r * 0.52, r * 0.52);
            dc.DrawEllipse(Br(inner), null, c, r * 0.30, r * 0.30);
            dc.DrawEllipse(Br(Gold), null, c, r * 0.12, r * 0.12);
        }

        private static void Diamond(DrawingContext dc, Point c, Vector along, Vector across, double halfLen, double halfWid, Color fill, Color edge, double edgeWidth)
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(c + along * halfLen, true, true);
                ctx.LineTo(c + across * halfWid, true, false);
                ctx.LineTo(c - along * halfLen, true, false);
                ctx.LineTo(c - across * halfWid, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(Br(fill), Pn(edge, edgeWidth), g);
        }

        private static Geometry Star(Point c, double outer, double inner, int points, double rotation)
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                for (int i = 0; i < points * 2; i++)
                {
                    double r = (i & 1) == 0 ? outer : inner;
                    double ang = rotation + Math.PI * i / points;
                    var p = new Point(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang));
                    if (i == 0) ctx.BeginFigure(p, true, true); else ctx.LineTo(p, true, false);
                }
            }
            g.Freeze();
            return g;
        }

        // A pointed oval with slightly convex sides: the classic medallion outline.
        private static Point[][] LozengeSegments(Point c, double a, double b) => new[]
        {
            new[] { new Point(c.X - a, c.Y), new Point(c.X - a * 0.86, c.Y - b * 0.34), new Point(c.X - a * 0.38, c.Y - b * 0.84), new Point(c.X, c.Y - b) },
            new[] { new Point(c.X, c.Y - b), new Point(c.X + a * 0.38, c.Y - b * 0.84), new Point(c.X + a * 0.86, c.Y - b * 0.34), new Point(c.X + a, c.Y) },
            new[] { new Point(c.X + a, c.Y), new Point(c.X + a * 0.86, c.Y + b * 0.34), new Point(c.X + a * 0.38, c.Y + b * 0.84), new Point(c.X, c.Y + b) },
            new[] { new Point(c.X, c.Y + b), new Point(c.X - a * 0.38, c.Y + b * 0.84), new Point(c.X - a * 0.86, c.Y + b * 0.34), new Point(c.X - a, c.Y) },
        };

        private static Geometry Lozenge(Point c, double a, double b)
        {
            Point[][] seg = LozengeSegments(c, a, b);
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(seg[0][0], true, true);
                foreach (Point[] s in seg) ctx.BezierTo(s[1], s[2], s[3], true, false);
            }
            g.Freeze();
            return g;
        }

        private static IEnumerable<Point> Sample(Point c, double a, double b, int perSegment)
        {
            foreach (Point[] s in LozengeSegments(c, a, b))
                for (int i = 0; i < perSegment; i++)
                {
                    double t = (i + 0.5) / perSegment, u = 1 - t;
                    yield return new Point(
                        u * u * u * s[0].X + 3 * u * u * t * s[1].X + 3 * u * t * t * s[2].X + t * t * t * s[3].X,
                        u * u * u * s[0].Y + 3 * u * u * t * s[1].Y + 3 * u * t * t * s[2].Y + t * t * t * s[3].Y);
                }
        }

        // ---------------------------------------------------------------- pixel pass

        private sealed class Noise
        {
            private readonly float[] _g;
            private readonly int _gw;
            private readonly double _cell;

            public Noise(int w, int h, double cell, int seed)
            {
                _cell = cell;
                _gw = (int)(w / cell) + 3;
                int gh = (int)(h / cell) + 3;
                var rnd = new Random(seed);
                _g = new float[_gw * gh];
                for (int i = 0; i < _g.Length; i++) _g[i] = (float)rnd.NextDouble();
            }

            public float At(double x, double y)
            {
                double fx = x / _cell, fy = y / _cell;
                int ix = (int)fx, iy = (int)fy;
                double tx = fx - ix, ty = fy - iy;
                tx = tx * tx * (3 - 2 * tx);
                ty = ty * ty * (3 - 2 * ty);
                float a = _g[iy * _gw + ix], b = _g[iy * _gw + ix + 1], c = _g[(iy + 1) * _gw + ix], d = _g[(iy + 1) * _gw + ix + 1];
                return (float)((a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty);
            }
        }

        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 65535f;
            }
        }

        private static byte Chan(float v, byte alpha) => (byte)Math.Min(alpha, Math.Max(0f, v));

        // Woven pile, hand-dyed variation, lighting, soft folds and the lumps hiding underneath.
        private static void Weave(byte[] px, int pw, int ph, double s, Rect body, StyleSpec spec)
        {
            int cell = Math.Max(2, (int)Math.Round(2.4 * s));
            var abrash = new Noise(pw, ph, 150 * s, 3);
            var fuzz = new Noise(pw, ph, 7 * s, 5);
            var tint = new Noise(pw, ph, 230 * s, 9);

            // (x, y, strength, width, angle): soft ridges as if the rug has been walked on.
            var folds = new (double x, double y, double amp, double w, double ang)[]
            {
                (0.22 * pw, 0.35 * ph,  0.075, 95 * s, -0.35),
                (0.58 * pw, 0.70 * ph, -0.070, 120 * s, 0.25),
                (0.82 * pw, 0.25 * ph,  0.065, 80 * s, 1.10),
            };

            // (x, y) as fractions of the rug, then radii in DIPs.
            var lumps = new (double fx, double fy, double rx, double ry)[]
            {
                (0.12, 0.30, 38, 26), (0.22, 0.72, 52, 34), (0.40, 0.18, 30, 22), (0.30, 0.55, 70, 18),
                (0.64, 0.80, 46, 30), (0.80, 0.30, 58, 36), (0.90, 0.66, 34, 24),
            };

            double edgeBand = 4 * s;
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    int i = (y * pw + x) * 4;
                    byte a = px[i + 3];
                    if (a == 0) continue;

                    float light = 1.07f - 0.13f * (x / (float)pw * 0.45f + y / (float)ph * 0.55f);
                    float m;

                    if (x < body.Left || x > body.Right)
                    {
                        m = light * (0.94f + 0.12f * Hash(x, y));    // fringe: just lighting and a little variation
                    }
                    else
                    {
                        m = 1f;
                        m += ((((x / cell + y / cell) & 1) == 0) ? 0.035f : -0.035f) * spec.Grid;   // over-under weave
                        if (x % cell == 0) m -= 0.055f * spec.LinesX;
                        if (y % cell == 0) m -= 0.040f * spec.LinesY;
                        m += (Hash(x, y) - 0.5f) * 0.09f * spec.Grain;
                        if (spec.Streak > 0) m += (Hash(x >> 2, y) - 0.5f) * spec.Streak;      // short fibres lying one way
                        m += (fuzz.At(x, y) - 0.5f) * 0.12f * spec.Fuzz;
                        m += (abrash.At(x, y) - 0.5f) * 0.20f * spec.Abrash;
                        m *= light;

                        foreach (var fo in folds)
                        {
                            double d = ((x - fo.x) * Math.Sin(fo.ang) - (y - fo.y) * Math.Cos(fo.ang)) / fo.w;
                            m += (float)(fo.amp * spec.Folds * 1.6 * d * Math.Exp(-d * d));
                        }

                        foreach (var lu in lumps)
                        {
                            double dx = (x - lu.fx * pw) / (lu.rx * s), dy = (y - lu.fy * ph) / (lu.ry * s);
                            double r2 = dx * dx + dy * dy;
                            if (r2 < 1)
                            {
                                double h = Math.Sqrt(1 - r2);
                                m += (float)(0.42 * spec.Lump * h * (-0.6 * dx - 0.8 * dy) - 0.05 * spec.Lump * (1 - h));
                            }
                        }

                        double edge = Math.Min(Math.Min(x - body.Left, body.Right - x), Math.Min(y - body.Top, body.Bottom - y));
                        if (edge < edgeBand) m *= (float)(0.90 + 0.10 * Math.Max(0, edge) / edgeBand);
                    }

                    float tn = tint.At(x, y) - 0.5f;
                    px[i + 2] = Chan(px[i + 2] * m * (1 + tn * 0.16f), a);   // R
                    px[i + 1] = Chan(px[i + 1] * m, a);                      // G
                    px[i + 0] = Chan(px[i + 0] * m * (1 - tn * 0.16f), a);   // B
                }
            }
        }

        // A tight soft shadow so the rug sits on the wallpaper instead of floating.
        // Blurred, offset copy of the rug's silhouette, 0..1.
        private static float[] ShadowPlane(byte[] px, int pw, int ph, int dx, int dy, int radius)
        {
            var a = new float[pw * ph];
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    int sx = x - dx, sy = y - dy;
                    if (sx >= 0 && sx < pw && sy >= 0 && sy < ph) a[y * pw + x] = px[(sy * pw + sx) * 4 + 3] / 255f;
                }

            var tmp = new float[a.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(a, tmp, pw, ph, radius, horizontal: true);
                BoxBlur(tmp, a, pw, ph, radius, horizontal: false);
            }
            return a;
        }

        // A tight shadow baked under the rug so it sits on the wallpaper instead of floating.
        private static void AddShadow(byte[] px, int pw, int ph, double s)
        {
            float[] a = ShadowPlane(px, pw, ph, (int)(3 * s), (int)(6 * s), Math.Max(2, (int)(6 * s)));
            for (int k = 0; k < a.Length; k++)
            {
                int i = k * 4;
                float shadow = a[k] * 0.55f * 255f;
                float ra = px[i + 3];
                px[i + 3] = (byte)Math.Min(255f, ra + shadow * (255f - ra) / 255f);
            }
        }

        private static void BoxBlur(float[] src, float[] dst, int w, int h, int r, bool horizontal)
        {
            int outer = horizontal ? h : w, inner = horizontal ? w : h;
            int stepInner = horizontal ? 1 : w, stepOuter = horizontal ? w : 1;
            float norm = 1f / (2 * r + 1);
            for (int o = 0; o < outer; o++)
            {
                int baseIdx = o * stepOuter;
                float sum = 0;
                for (int k = -r; k <= r; k++) sum += src[baseIdx + Math.Min(inner - 1, Math.Max(0, k)) * stepInner];
                for (int k = 0; k < inner; k++)
                {
                    dst[baseIdx + k * stepInner] = sum * norm;
                    int add = Math.Min(inner - 1, k + r + 1), sub = Math.Max(0, k - r);
                    sum += src[baseIdx + add * stepInner] - src[baseIdx + sub * stepInner];
                }
            }
        }
    }
}
