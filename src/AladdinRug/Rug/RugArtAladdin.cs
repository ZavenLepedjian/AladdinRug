using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AladdinRug
{
    /// <summary>A little Aladdin sitting cross-legged on the rug, seen from above, with his lamp beside him.</summary>
    internal static partial class RugArt
    {
        private static readonly Color Fez = Rgb(0xC8282E), FezDark = Rgb(0x8F1A20), Tassel = Rgb(0x1F4E9C), Skin = Rgb(0xD9A272), SkinShade = Rgb(0xB07C4E),
                                      Vest = Rgb(0x7A3FA0), VestDark = Rgb(0x57297A), Trim = Rgb(0xE6BC48), Pants = Rgb(0xF3ECDD), PantsFold = Rgb(0xCDC1A8),
                                      Cuff = Rgb(0x2FA3A0), Shoe = Rgb(0xD9AA3F), Brass = Rgb(0xE2B33C), BrassDark = Rgb(0x9A6F1C);

        /// <summary>Paint him over the finished rug picture (premultiplied BGRA), after the weave so he isn't woven into it.</summary>
        private static void AddAladdin(byte[] px, int pw, int ph, double s, StyleSpec spec)
        {
            Rect body = BodyRect(pw, ph, s);
            var at = new Point(body.Left + body.Width * spec.AladdinX, body.Top + body.Height * spec.AladdinY);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(at.X, at.Y));
                dc.PushTransform(new RotateTransform(-9));
                double k = s * spec.AladdinSize * 0.92;
                dc.PushTransform(new ScaleTransform(k, k));
                DrawAladdin(dc);
                dc.Pop(); dc.Pop(); dc.Pop();
            }

            var target = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var over = new byte[pw * ph * 4];
            target.CopyPixels(over, pw * 4, 0);

            for (int i = 0; i < px.Length; i += 4)
            {
                int a = over[i + 3];
                if (a == 0) continue;
                int keep = 255 - a;
                for (int c = 0; c < 4; c++) px[i + c] = (byte)Math.Min(255, over[i + c] + px[i + c] * keep / 255);
            }
        }

        private static void Ell(DrawingContext dc, Brush fill, Pen pen, double cx, double cy, double rx, double ry, double angle = 0)
        {
            if (angle != 0) dc.PushTransform(new RotateTransform(angle, cx, cy));
            dc.DrawEllipse(fill, pen, new Point(cx, cy), rx, ry);
            if (angle != 0) dc.Pop();
        }

        private static void Limb(DrawingContext dc, Color color, double width, params Point[] pts)
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, Pn(color, width, 1, PenLineCap.Round), g);
        }

        // Units are DIPs; (0,0) is where he sits, and he faces down the screen.
        private static void DrawAladdin(DrawingContext dc)
        {
            // soft shadow on the rug: the main shapes, grown a little and a little more, faintly
            for (int grow = 3; grow >= 0; grow--)
            {
                Brush sh = Br(Color.FromRgb(0, 0, 0), 0.10);
                double g = grow * 1.8;
                Ell(dc, sh, null, -14 + 2.5, 22 + 4, 24 + g, 12 + g, 28);
                Ell(dc, sh, null, 14 + 2.5, 22 + 4, 24 + g, 12 + g, -28);
                Ell(dc, sh, null, 2.5, -1 + 4, 25 + g, 13 + g);
                Ell(dc, sh, null, 2.5, -9 + 4, 11 + g, 11 + g);
                Ell(dc, sh, null, 56 + 2.5, 20 + 4, 16 + g, 9 + g, -12);
            }

            // legs: thighs go out to the knees, shins cross back over each other, shoes with curled tips
            Pen pantsEdge = Pn(PantsFold, 0.8);
            Limb(dc, PantsFold, 17, new Point(-10, 8), new Point(-33, 24));
            Limb(dc, Pants, 15, new Point(-10, 8), new Point(-33, 24));
            Limb(dc, PantsFold, 17, new Point(10, 8), new Point(33, 24));
            Limb(dc, Pants, 15, new Point(10, 8), new Point(33, 24));
            Ell(dc, Br(PantsFold), null, 0, 21, 27, 12.5);                              // the lap between the knees
            Ell(dc, Br(Pants), null, 0, 20.5, 25.5, 11);
            Limb(dc, PantsFold, 14, new Point(-33, 24), new Point(5, 33));
            Limb(dc, Pants, 12, new Point(-33, 24), new Point(5, 33));
            Limb(dc, PantsFold, 14, new Point(33, 24), new Point(-5, 35));
            Limb(dc, Pants, 12, new Point(33, 24), new Point(-5, 35));
            Limb(dc, PantsFold, 0.9, new Point(-27, 20), new Point(-14, 15));          // creases in the baggy cloth
            Limb(dc, PantsFold, 0.9, new Point(27, 20), new Point(14, 15));
            Limb(dc, PantsFold, 0.9, new Point(-20, 27), new Point(-4, 31));
            Ell(dc, Br(Cuff), Pn(BrassDark, 0.6), 4, 33.5, 4.2, 5.6, 80);               // cuffs at the ankles
            Ell(dc, Br(Cuff), Pn(BrassDark, 0.6), -4, 35.5, 4.2, 5.6, 100);
            Ell(dc, Br(Shoe), Pn(BrassDark, 0.8), 11, 33, 6.5, 3.8, -8);                // pointed shoes
            Ell(dc, Br(Shoe), Pn(BrassDark, 0.8), -11, 35.5, 6.5, 3.8, 8);
            Ell(dc, Br(Shoe), null, 17, 31.5, 2.2, 2.2);
            Ell(dc, Br(Shoe), null, -17, 34, 2.2, 2.2);

            // arms resting on his knees
            Limb(dc, SkinShade, 9.6, new Point(-22, -1), new Point(-30, 11), new Point(-25, 24));
            Limb(dc, Skin, 8, new Point(-22, -1), new Point(-30, 11), new Point(-25, 24));
            Limb(dc, SkinShade, 9.6, new Point(22, -1), new Point(30, 11), new Point(25, 24));
            Limb(dc, Skin, 8, new Point(22, -1), new Point(30, 11), new Point(25, 24));
            Ell(dc, Br(Skin), Pn(SkinShade, 0.6), -25, 25, 4.4, 4.4);
            Ell(dc, Br(Skin), Pn(SkinShade, 0.6), 25, 25, 4.4, 4.4);

            // his vest, seen from behind, with a gold edge
            Ell(dc, Br(VestDark), null, 0, 0.5, 26, 14.5);
            Ell(dc, Br(Vest), Pn(Trim, 1.4), 0, -0.5, 25, 13.5);
            Limb(dc, VestDark, 1, new Point(0, -6), new Point(0, 12));
            Ell(dc, Br(Rgb(0x9658B8)), null, -8, -5, 8, 4, -20);                          // light catching a shoulder

            // head: ears, then the red fez with its blue tassel
            Ell(dc, Br(Skin), Pn(SkinShade, 0.6), -10.5, -9, 2.6, 3.6);
            Ell(dc, Br(Skin), Pn(SkinShade, 0.6), 10.5, -9, 2.6, 3.6);
            Ell(dc, Br(FezDark), null, 0, -9, 11.5, 11.5);
            Ell(dc, Br(Fez), null, 0, -9.5, 10.5, 10.5);
            Ell(dc, new RadialGradientBrush(Rgb(0xE24A4F), Fez) { GradientOrigin = new Point(0.35, 0.3) }, null, 0, -9.5, 8.5, 8.5);
            Ell(dc, Br(Brass), Pn(BrassDark, 0.5), 0, -9.5, 2.1, 2.1);                    // the button on top
            Limb(dc, Tassel, 1.3, new Point(0, -9.5), new Point(6, -4), new Point(9.5, 1));
            Ell(dc, Br(Tassel), null, 9.5, 2, 2, 3.2, -25);

            DrawLamp(dc);
        }

        // His lamp beside him, with a wisp of blue smoke curling up from the spout.
        private static void DrawLamp(DrawingContext dc)
        {
            dc.PushTransform(new TranslateTransform(56, 20));
            dc.PushTransform(new RotateTransform(-12));

            // smoke first, so it rises from behind the spout
            for (int i = 0; i < 7; i++)
            {
                double t = i / 6.0;
                double x = -16 - 14 * t + 7 * Math.Sin(t * 5), y = -3 - 24 * t;
                dc.DrawEllipse(Br(Rgb(0x6FB6F2), 0.62 * (1 - t * 0.6)), null, new Point(x, y), 3.5 + 5.5 * t, 3.5 + 5.5 * t);
            }

            var body = new LinearGradientBrush(Rgb(0xF5D060), Rgb(0xB9871F), 90);
            body.Freeze();
            // handle, spout, body, lid
            dc.DrawGeometry(null, Pn(BrassDark, 3.2, 1, PenLineCap.Round), HandleGeometry());
            dc.DrawGeometry(null, Pn(Brass, 1.8, 1, PenLineCap.Round), HandleGeometry());
            var spout = new StreamGeometry();
            using (StreamGeometryContext ctx = spout.Open())
            {
                ctx.BeginFigure(new Point(-8, -3), true, true);
                ctx.BezierTo(new Point(-14, -3), new Point(-17, -5), new Point(-20, -7), true, false);
                ctx.LineTo(new Point(-18, -3), true, false);
                ctx.BezierTo(new Point(-15, 1), new Point(-12, 3), new Point(-8, 3), true, false);
            }
            spout.Freeze();
            dc.DrawGeometry(body, Pn(BrassDark, 0.9), spout);
            Ell(dc, body, Pn(BrassDark, 1), 0, 0, 13, 8);
            Ell(dc, Br(Brass), Pn(BrassDark, 0.8), 0, 0, 6.5, 4);
            Ell(dc, Br(Rgb(0xFFF1B0)), null, -3, -2.5, 2.6, 1.3, -15);                  // a glint
            Ell(dc, Br(Rgb(0xCE3B3B)), Pn(BrassDark, 0.5), 0, 0, 1.7, 1.7);              // jewel
            dc.Pop(); dc.Pop();
        }

        private static Geometry HandleGeometry()
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(new Point(11, -3), false, false);
                ctx.BezierTo(new Point(21, -8), new Point(23, 7), new Point(11, 3), true, false);
            }
            g.Freeze();
            return g;
        }
    }
}
