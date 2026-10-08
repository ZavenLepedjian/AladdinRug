using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AladdinRug
{
    /// <summary>The other rugs: a flat-woven kilim, a shag, and a doormat.</summary>
    internal static partial class RugArt
    {
        // ---------------------------------------------------------------- kilim

        private static readonly Color Terracotta = Rgb(0xB4492F), Indigo = Rgb(0x23325C), IndigoDeep = Rgb(0x16203D),
                                      KCream = Rgb(0xE6D9B6), Mustard = Rgb(0xD1A238), Madder = Rgb(0x8A2A25), Olive = Rgb(0x6D7440);

        private static void DrawKilim(DrawingContext dc, int pw, int ph, double s, Random rnd)
        {
            Rect body = BodyRect(pw, ph, s);
            DrawFringe(dc, body, s, rnd);

            Fill(dc, IndigoDeep, body);                                 // selvedge
            Fill(dc, KCream, Inset(body, 4 * s));
            Fill(dc, Indigo, Inset(body, 7 * s));                       // outer border
            Rect border = Inset(body, 7 * s), inner = Inset(body, 41 * s);
            ZigZag(dc, Inset(body, 24 * s), 12 * s, s, KCream, Mustard);
            Fill(dc, KCream, Inset(body, 41 * s));
            Fill(dc, Madder, Inset(body, 44 * s));

            Rect field = Inset(body, 52 * s);
            Fill(dc, Terracotta, field);
            dc.DrawRectangle(null, Pn(Mustard, 1.6 * s), Inset(field, -2.5 * s));

            // bands of small blocks across the top and bottom of the field
            double bandH = 18 * s, block = 11 * s;
            Color[] cycle = { Indigo, KCream, Mustard, KCream };
            for (int row = 0; row < 2; row++)
            {
                double y = row == 0 ? field.Top : field.Bottom - bandH;
                Fill(dc, IndigoDeep, new Rect(field.Left, y, field.Width, bandH));
                int n = (int)(field.Width / block);
                double w = field.Width / n;
                for (int i = 0; i < n; i++)
                {
                    Color c = cycle[(i + row) % cycle.Length];
                    dc.DrawRectangle(Br(c), null, new Rect(field.Left + i * w + 1.5 * s, y + 3 * s, w - 3 * s, bandH - 6 * s));
                }
            }

            // a row of stepped medallions
            var cell = new Rect(field.Left, field.Top + bandH + 6 * s, field.Width, field.Height - 2 * bandH - 12 * s);
            int count = Math.Max(1, (int)Math.Round(cell.Width / (cell.Height * 1.1)));
            double spacing = cell.Width / count;
            double r = Math.Min(cell.Height / 2 * 0.94, spacing / 2 * 0.86);

            // scattered dots on the plain ground
            var dot = Br(Indigo, 0.55);
            double gap = 19 * s;
            for (double y = cell.Top + gap / 2; y < cell.Bottom; y += gap)
                for (double x = cell.Left + gap / 2; x < cell.Right; x += gap)
                    dc.DrawRectangle(dot, null, new Rect(x - 1.5 * s, y - 1.5 * s, 3 * s, 3 * s));

            for (int i = 0; i < count; i++)
            {
                var c = new Point(cell.Left + (i + 0.5) * spacing, cell.Top + cell.Height / 2);
                StepDiamond(dc, c, r * 1.07, Terracotta);                // clears the dots beneath
                StepDiamond(dc, c, r, Indigo);
                StepDiamond(dc, c, r * 0.84, KCream);
                StepDiamond(dc, c, r * 0.70, Madder);
                StepDiamond(dc, c, r * 0.54, Mustard);
                StepDiamond(dc, c, r * 0.38, Indigo);
                StepDiamond(dc, c, r * 0.22, KCream);
                StepDiamond(dc, c, r * 0.10, Terracotta);

                double hook = r / 6;                                     // little steps on each tip
                foreach (var d in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    double px = c.X + d.Item1 * (r + hook * 0.6), py = c.Y + d.Item2 * (r + hook * 0.6);
                    dc.DrawRectangle(Br(KCream), null, new Rect(px - hook * 0.9, py - hook * 0.9, hook * 1.8, hook * 1.8));
                    dc.DrawRectangle(Br(Madder), null, new Rect(px - hook * 0.45, py - hook * 0.45, hook * 0.9, hook * 0.9));
                }
                if (i > 0)                                               // between neighbours
                {
                    var b = new Point(cell.Left + i * spacing, cell.Top + cell.Height / 2);
                    StepDiamond(dc, b, r * 0.30, Olive);
                    StepDiamond(dc, b, r * 0.18, KCream);
                    StepDiamond(dc, b, r * 0.07, Indigo);
                }
            }

            dc.DrawRectangle(null, Pn(Color.FromRgb(0, 0, 0), 1.4 * s, 0.45), body);
        }

        // A diamond whose edges are stairs, as in a woven (not knotted) rug: built from rows of rectangles.
        private static void StepDiamond(DrawingContext dc, Point c, double r, Color color)
        {
            const int steps = 6;
            double st = r / steps;
            Brush brush = Br(color);
            for (int j = -steps; j < steps; j++)
            {
                double half = (steps - Math.Abs(j + 0.5)) * st;
                dc.DrawRectangle(brush, null, new Rect(c.X - half, c.Y + j * st, 2 * half, st + 0.6));
            }
        }

        // A band with a row of triangles along it (the "running dog" of the border).
        private static void ZigZag(DrawingContext dc, Rect centre, double width, double s, Color tooth, Color dot)
        {
            Point[] corners = { centre.TopLeft, centre.TopRight, centre.BottomRight, centre.BottomLeft };
            for (int k = 0; k < 4; k++)
            {
                Point a = corners[k], b = corners[(k + 1) % 4];
                Vector d = b - a;
                double len = d.Length;
                d.Normalize();
                var nrm = new Vector(-d.Y, d.X);
                int n = Math.Max(2, (int)Math.Round(len / (width * 1.1)));
                double step = len / n;
                for (int i = 0; i < n; i++)
                {
                    Point p0 = a + d * (i * step), p1 = a + d * ((i + 1) * step), mid = a + d * ((i + 0.5) * step);
                    var g = new StreamGeometry();
                    using (StreamGeometryContext ctx = g.Open())
                    {
                        ctx.BeginFigure(p0 - nrm * width / 2, true, true);
                        ctx.LineTo(p1 - nrm * width / 2, true, false);
                        ctx.LineTo(mid + nrm * width / 2, true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(Br(tooth), null, g);
                    dc.DrawEllipse(Br(dot), null, mid - nrm * width * 0.30, 2.2 * s, 2.2 * s);
                }
            }
        }

        // ---------------------------------------------------------------- shag

        private static readonly Color Oat = Rgb(0xE3D9C4), Straw = Rgb(0xD0C3A6), Pearl = Rgb(0xF0E9D8);

        private static double ShagRadius(double s) => 28 * s;

        private static void DrawShag(DrawingContext dc, int pw, int ph, double s, Random rnd)
        {
            Rect body = BodyRect(pw, ph, s);
            double rad = ShagRadius(s);

            var fill = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.45, 0.4), RadiusX = 0.75, RadiusY = 0.85 };
            fill.GradientStops.Add(new GradientStop(Pearl, 0));
            fill.GradientStops.Add(new GradientStop(Oat, 0.65));
            fill.GradientStops.Add(new GradientStop(Straw, 1));
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, body, rad, rad);

            // soft waves of slightly different pile, like a hand-tufted rug
            Color[] rings = { Straw, Pearl, Straw };
            for (int i = 0; i < 3; i++)
            {
                double d = (26 + 22 * i) * s;
                dc.DrawRoundedRectangle(null, Pn(rings[i], 9 * s, 0.50), Inset(body, d), Math.Max(2, rad - d * 0.6), Math.Max(2, rad - d * 0.6));
            }

            // strands standing out round the edge
            double top = body.Width - 2 * rad, side = body.Height - 2 * rad, arc = Math.PI / 2 * rad;
            double total = 2 * top + 2 * side + 4 * arc;
            for (int i = 0; i < 1700; i++)
            {
                PerimeterPoint(body, rad, rnd.NextDouble() * total, top, side, arc, out Point p, out Vector n);
                double len = (2.5 + 7 * rnd.NextDouble()) * s;
                var tangent = new Vector(-n.Y, n.X);
                Point q = p + n * len + tangent * ((rnd.NextDouble() - 0.5) * 4 * s);
                dc.DrawLine(Pn(Lerp(Straw, Pearl, rnd.NextDouble()), (0.7 + 0.8 * rnd.NextDouble()) * s, 0.85, PenLineCap.Round), p - n * 1.5 * s, q);
            }
        }

        private static void PerimeterPoint(Rect b, double rad, double t, double top, double side, double arc, out Point p, out Vector n)
        {
            if (t < top) { p = new Point(b.Left + rad + t, b.Top); n = new Vector(0, -1); return; }
            t -= top;
            if (t < arc) { double a = -Math.PI / 2 + t / rad; n = new Vector(Math.Cos(a), Math.Sin(a)); p = new Point(b.Right - rad, b.Top + rad) + n * rad; return; }
            t -= arc;
            if (t < side) { p = new Point(b.Right, b.Top + rad + t); n = new Vector(1, 0); return; }
            t -= side;
            if (t < arc) { double a = t / rad; n = new Vector(Math.Cos(a), Math.Sin(a)); p = new Point(b.Right - rad, b.Bottom - rad) + n * rad; return; }
            t -= arc;
            if (t < top) { p = new Point(b.Right - rad - t, b.Bottom); n = new Vector(0, 1); return; }
            t -= top;
            if (t < arc) { double a = Math.PI / 2 + t / rad; n = new Vector(Math.Cos(a), Math.Sin(a)); p = new Point(b.Left + rad, b.Bottom - rad) + n * rad; return; }
            t -= arc;
            if (t < side) { p = new Point(b.Left, b.Bottom - rad - t); n = new Vector(-1, 0); return; }
            t -= side;
            double a2 = Math.PI + t / rad;
            n = new Vector(Math.Cos(a2), Math.Sin(a2));
            p = new Point(b.Left + rad, b.Top + rad) + n * rad;
        }

        // Distance inside a rounded rectangle's outline (negative outside it).
        private static double InsideRounded(double x, double y, Rect b, double rad)
        {
            double cx = (b.Left + b.Right) / 2, cy = (b.Top + b.Bottom) / 2;
            double qx = Math.Abs(x - cx) - (b.Width / 2 - rad), qy = Math.Abs(y - cy) - (b.Height / 2 - rad);
            double outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
            double inside = Math.Min(Math.Max(qx, qy), 0);
            return rad - (outside + inside);
        }

        // Thousands of strands: bright tips, dark roots between them, clumps, and a ragged edge.
        private static void ShagWeave(byte[] px, int pw, int ph, double s, Rect body, StyleSpec spec)
        {
            var tuft = new Noise(pw, ph, 2.6 * s, 21);
            var clumps = new Noise(pw, ph, 7 * s, 22);
            var broad = new Noise(pw, ph, 120 * s, 23);
            double rad = ShagRadius(s);

            var folds = new (double x, double y, double amp, double w, double ang)[]
            {
                (0.25 * pw, 0.40 * ph,  0.07, 110 * s, -0.3),
                (0.70 * pw, 0.65 * ph, -0.06, 130 * s, 0.5),
            };
            var lumps = new (double fx, double fy, double rx, double ry)[]
            {
                (0.16, 0.34, 38, 26), (0.26, 0.70, 52, 34), (0.42, 0.20, 30, 22), (0.33, 0.55, 70, 18),
                (0.66, 0.78, 46, 30), (0.80, 0.32, 58, 36), (0.88, 0.62, 34, 24),
            };

            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    int i = (y * pw + x) * 4;
                    byte a = px[i + 3];
                    if (a == 0) continue;

                    float light = 1.07f - 0.13f * (x / (float)pw * 0.45f + y / (float)ph * 0.55f);
                    double edge = InsideRounded(x, y, body, rad);
                    float n1 = Hash(x, y);
                    float m;
                    float keep = 1f;

                    if (edge < 0)
                    {
                        m = light * (0.90f + 0.20f * n1);                      // stray strands past the outline
                    }
                    else
                    {
                        float cl = clumps.At(x, y), tf = tuft.At(x, y);
                        m = 1f + (n1 - 0.5f) * 0.20f + (tf - 0.5f) * 0.40f + (cl - 0.5f) * 0.28f + (broad.At(x, y) - 0.5f) * 0.14f;
                        if (n1 < 0.05f) m *= 0.86f;                           // a dark root between the strands
                        else if (n1 > 0.95f) m *= 1.08f;                      // a bright tip catching the light
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

                        double band = 5 * s;
                        if (edge < band) keep = (float)Math.Max(0, Math.Min(1, (edge + (cl - 0.5) * 4 * s + (n1 - 0.5) * 3 * s) / (3.5 * s) + 0.15));
                    }

                    float tn = broad.At(x + 300, y + 200) - 0.5f;
                    byte na = (byte)(a * keep);
                    float f = m * keep;
                    px[i + 2] = Chan(px[i + 2] * f * (1 + tn * 0.08f), na);
                    px[i + 1] = Chan(px[i + 1] * f, na);
                    px[i + 0] = Chan(px[i + 0] * f * (1 - tn * 0.08f), na);
                    px[i + 3] = na;
                }
        }

        // ---------------------------------------------------------------- doormat

        private static void DrawDoormat(DrawingContext dc, int pw, int ph, double s, Random rnd)
        {
            Rect body = BodyRect(pw, ph, s);
            double rad = 7 * s;

            var coir = new LinearGradientBrush(Rgb(0x9A7A4F), Rgb(0x84643D), 90);
            coir.Freeze();
            dc.DrawRoundedRectangle(Br(Rgb(0x2B1D12)), null, body, rad, rad);                       // rubber rim
            dc.DrawRoundedRectangle(coir, null, Inset(body, 6 * s), rad * 0.6, rad * 0.6);
            dc.DrawRoundedRectangle(null, Pn(Rgb(0x5A4128), 2.2 * s), Inset(body, 14 * s), 3 * s, 3 * s);

            // The only honest thing to put on a mat that has things hidden under it.
            Rect inner = Inset(body, 24 * s);
            string[] lines = { "NOTHING TO", "SEE HERE" };
            var face = new Typeface(new FontFamily("Arial Black, Segoe UI Black, Impact"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
            double size = 100;
            double widest = 0;
            foreach (string line in lines)
                widest = Math.Max(widest, new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0).Width);
            size = Math.Min(size * inner.Width / widest, inner.Height / 2.35);

            double lineH = size * 1.12, y = inner.Top + (inner.Height - lineH * lines.Length) / 2 + size * 0.04;
            foreach (string line in lines)
            {
                var ft = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Br(Rgb(0x2B1D12)), 1.0);
                dc.DrawText(ft, new Point(inner.Left + (inner.Width - ft.Width) / 2, y));
                y += lineH;
            }
        }
    }
}
