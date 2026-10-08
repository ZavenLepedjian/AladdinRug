using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AladdinRug
{
    /// <summary>How the rug merchant stands, in the units of <see cref="MerchantArt"/> (he faces right before being turned by Heading).</summary>
    internal struct MerchantPose
    {
        public double Heading;      // degrees, 0 = facing right (screen x), 90 = down
        public double Phase;        // walking cycle, radians
        public double Walk;         // 0 = standing still, 1 = striding
        public double Lean;         // 0..1 bending forward over the work
        public double Crouch;       // 0..1 down on his haunches
        public double Reach;        // 0..1 arms stretched out in front (to push), or clapping
        public double Broom;        // 1 = he has a broom in his hands
        public double BroomSwing;   // -1..1 which way the broom head is swept across
        public int Carry;           // how many files he is carrying (an armful, 0 to 5)
        public double CarryHue;     // the colour of that kind of file (degrees)
    }

    /// <summary>A rug merchant seen from straight above: turban, vest, baggy trousers, curled shoes.</summary>
    internal static class MerchantArt
    {
        private static Color C(int v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        private static Brush Br(Color c, double a = 1) { var b = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)); b.Freeze(); return b; }
        private static Pen Pn(Color c, double w, PenLineCap cap = PenLineCap.Round, double a = 1) { var p = new Pen(Br(c, a), w) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }

        private static readonly Color Turban = C(0xF1E9D6), TurbanFold = C(0xCDBFA2), Ruby = C(0xB8232F), Vest = C(0x1F7A7A), VestDark = C(0x145555), Gold = C(0xE0B240),
                                      Trousers = C(0xB65A32), TrousersDark = C(0x8C3F20), Skin = C(0xB98058), SkinDark = C(0x8F5F3C), Shoe = C(0x6E4326), Tip = C(0xE0B240);

        private static void Ell(DrawingContext dc, Brush fill, Pen pen, double x, double y, double rx, double ry, double angle = 0)
        {
            if (angle != 0) dc.PushTransform(new RotateTransform(angle, x, y));
            dc.DrawEllipse(fill, pen, new Point(x, y), rx, ry);
            if (angle != 0) dc.Pop();
        }

        private static void Limb(DrawingContext dc, Color c, double w, params Point[] pts)
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, Pn(c, w), g);
        }

        /// <summary>Draw him centred at (cx, cy); k is the size of one unit in the drawing's own units.</summary>
        public static void Draw(DrawingContext dc, MerchantPose p, double cx, double cy, double k)
        {
            dc.PushTransform(new TranslateTransform(cx, cy));

            // soft shadow on the floor (doesn't turn with him)
            for (int g = 3; g >= 0; g--)
                dc.DrawEllipse(Br(Color.FromRgb(0, 0, 0), 0.085), null, new Point(3 * k, 5 * k), (24 + g * 2.2) * k, (22 + g * 2.2) * k);

            dc.PushTransform(new RotateTransform(p.Heading));
            dc.PushTransform(new ScaleTransform(k, k));

            double w = p.Walk, c = p.Crouch, l = p.Lean, r = p.Reach, ph = p.Phase;
            double sinP = Math.Sin(ph);

            // ---- legs and shoes
            double stride = 15 * w * (1 - 0.75 * c);
            Point footL = new Point(-1 + stride * sinP, -8 - 4 * c), footR = new Point(-1 - stride * sinP, 8 + 4 * c);
            Point hipL = new Point(-3, -6), hipR = new Point(-3, 6);
            Point kneeL = new Point(Math.Max(footL.X, -3) + 3 + 5 * c, -10 - 6 * c), kneeR = new Point(Math.Max(footR.X, -3) + 3 + 5 * c, 10 + 6 * c);
            Limb(dc, TrousersDark, 13.5, hipL, kneeL, footL);
            Limb(dc, Trousers, 12, hipL, kneeL, footL);
            Limb(dc, TrousersDark, 13.5, hipR, kneeR, footR);
            Limb(dc, Trousers, 12, hipR, kneeR, footR);
            foreach (Point f in new[] { footL, footR })
            {
                Ell(dc, Br(Shoe), Pn(Color.FromRgb(0x3E, 0x24, 0x12), 0.7), f.X + 3.5, f.Y, 7.5, 4.2);
                Ell(dc, Br(Tip), null, f.X + 10.5, f.Y, 2.2, 2.2);                      // the curled toe
            }

            // ---- the broom, held out in front and swept from side to side
            if (p.Broom > 0)
            {
                double bx = -1 + 7 * l + 2 * c, sweep = p.BroomSwing * 15;
                var grip = new Point(bx + 8, 0);
                var head = new Point(bx + 46, sweep);
                double ang = Math.Atan2(head.Y - grip.Y, head.X - grip.X) * 180 / Math.PI;
                Limb(dc, Color.FromRgb(0x4A, 0x30, 0x1A), 3.6, grip, head);
                Limb(dc, Color.FromRgb(0x9A, 0x6C, 0x3A), 2.4, grip, head);
                dc.PushTransform(new TranslateTransform(head.X, head.Y));
                dc.PushTransform(new RotateTransform(ang));
                var bristles = new StreamGeometry();
                using (StreamGeometryContext ctx = bristles.Open())
                {
                    ctx.BeginFigure(new Point(-2, -4), true, true);
                    ctx.LineTo(new Point(-2, 4), true, false);
                    ctx.LineTo(new Point(15, 13), true, false);
                    ctx.LineTo(new Point(15, -13), true, false);
                }
                bristles.Freeze();
                dc.DrawGeometry(Br(Color.FromRgb(0xD9, 0xB3, 0x5A)), Pn(Color.FromRgb(0x8A, 0x65, 0x22), 0.8), bristles);
                for (int i = -5; i <= 5; i++)
                    dc.DrawLine(Pn(Color.FromRgb(0xA8, 0x83, 0x2F), 0.7), new Point(2, i * 0.7), new Point(15, i * 2.3));
                dc.DrawRectangle(Br(Ruby), null, new Rect(-3, -4.2, 3.2, 8.4));               // the cord that ties the bristles
                dc.Pop(); dc.Pop();
            }

            // ---- an armful of files held out in front, fanned like cards
            if (p.Carry > 0)
            {
                Color tab = FromHue(p.CarryHue);
                int n = Math.Min(5, p.Carry);
                for (int card = 0; card < n; card++)
                {
                    double ang = (card - (n - 1) / 2.0) * 11;
                    dc.PushTransform(new TranslateTransform(-1 + 7 * l + 2 * c + 12, 0));
                    dc.PushTransform(new RotateTransform(ang, 0, 0));
                    dc.DrawRectangle(Br(Color.FromRgb(0xF7, 0xF4, 0xEA)), Pn(Color.FromRgb(0x8C, 0x86, 0x78), 0.7), new Rect(0, -6.5, 15, 13));
                    dc.DrawRectangle(Br(tab), null, new Rect(0, -6.5, 4.2, 13));
                    for (int ln = 0; ln < 3; ln++) dc.DrawLine(Pn(Color.FromRgb(0xB5, 0xAF, 0xA0), 0.6), new Point(6, -3.5 + ln * 3.4), new Point(13, -3.5 + ln * 3.4));
                    dc.Pop(); dc.Pop();
                }
            }

            // ---- upper body sways a little as he walks
            dc.PushTransform(new RotateTransform(4 * w * Math.Sin(ph + 1.5708)));
            double tx = -1 + 7 * l + 2 * c;

            // arms: swinging while walking, stretched out in front when pushing or clapping
            foreach (int side in new[] { -1, 1 })
            {
                double swing = side * -9 * w * sinP;
                var freeHand = new Point(tx + 2 + swing, side * (23 - 2 * w));
                var reachHand = new Point(tx + 10 + 13 * r, side * (11 - 3.5 * r));
                var hand = new Point(freeHand.X + (reachHand.X - freeHand.X) * r, freeHand.Y + (reachHand.Y - freeHand.Y) * r);
                var shoulder = new Point(tx, side * 18);
                var elbow = new Point((shoulder.X + hand.X) / 2 + 1, (shoulder.Y + hand.Y) / 2 + side * 4 * (1 - r));
                Limb(dc, SkinDark, 8.4, shoulder, elbow, hand);
                Limb(dc, Skin, 7, shoulder, elbow, hand);
                Ell(dc, Br(Skin), Pn(SkinDark, 0.6), hand.X, hand.Y, 3.6, 3.6);
            }

            // vest seen from behind
            Ell(dc, Br(VestDark), null, tx, 0, 12.6, 21.4);
            Ell(dc, Br(Vest), Pn(Gold, 1.3), tx, 0, 11.6, 20.4);
            Limb(dc, VestDark, 1, new Point(tx - 9, 0), new Point(tx + 4, 0));
            Ell(dc, Br(C(0x2FA3A3)), null, tx + 2, -9, 5, 8, -15);                          // light on a shoulder

            // head: ears, a hint of nose and brow, then the turban
            double hx = tx + 2 + 3 * l;
            Ell(dc, Br(Skin), Pn(SkinDark, 0.6), hx, -10.2, 2.5, 3.2);
            Ell(dc, Br(Skin), Pn(SkinDark, 0.6), hx, 10.2, 2.5, 3.2);
            Ell(dc, Br(Skin), Pn(SkinDark, 0.6), hx + 10.3, 0, 2.6, 3.4);
            Ell(dc, Br(TurbanFold), null, hx, 0, 11.8, 11.8);
            Ell(dc, Br(Turban), null, hx, 0, 10.8, 10.8);
            // the wound cloth: a spiral of folds
            for (int i = 0; i < 3; i++)
            {
                double rr = 9.6 - i * 3.0;
                dc.PushTransform(new RotateTransform(i * 70 + 20, hx, 0));
                dc.DrawArc(Pn(TurbanFold, 1.1, PenLineCap.Round, 0.9), hx, 0, rr, 15, 250);
                dc.Pop();
            }
            Ell(dc, new RadialGradientBrush(Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)) { GradientOrigin = new Point(0.35, 0.3) }, null, hx, 0, 9, 9);
            Ell(dc, Br(Ruby), Pn(Gold, 0.9), hx + 4.2, 0, 2.3, 2.3);                         // jewel on the front of the turban
            // a loose tail of cloth flicking behind
            double flick = 3 * w * Math.Sin(2 * ph);
            Limb(dc, TurbanFold, 2.4, new Point(hx - 8, 0), new Point(hx - 13, 2 + flick), new Point(hx - 17, 3 + 2 * flick));
            Limb(dc, Turban, 1.5, new Point(hx - 8, 0), new Point(hx - 13, 2 + flick), new Point(hx - 17, 3 + 2 * flick));

            dc.Pop();   // sway
            dc.Pop(); dc.Pop(); dc.Pop();
        }

        private static Color FromHue(double hue)
        {
            double h = (hue % 360) / 60, f = h - Math.Floor(h), s = 0.55, v = 0.88, pp = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            double r, g, b;
            switch ((int)h) { case 0: r = v; g = t; b = pp; break; case 1: r = q; g = v; b = pp; break; case 2: r = pp; g = v; b = t; break; case 3: r = pp; g = q; b = v; break; case 4: r = t; g = pp; b = v; break; default: r = v; g = pp; b = q; break; }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        // An arc of a circle as a stroke (angles in degrees).
        private static void DrawArc(this DrawingContext dc, Pen pen, double cx, double cy, double r, double from, double sweep)
        {
            double a0 = from * Math.PI / 180, a1 = (from + sweep) * Math.PI / 180;
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)), false, false);
                ctx.ArcTo(new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    /// <summary>A small see-through window that carries the merchant over the desktop, just above the rug.</summary>
    internal sealed class MerchantWindow : Window
    {
        private sealed class Visual : FrameworkElement
        {
            public MerchantPose Pose;
            public double UnitPx = 1;        // pixels per drawing unit
            public int SizePx = 100;
            protected override void OnRender(DrawingContext dc)
            {
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double centre = SizePx / 2.0 / dpi;
                MerchantArt.Draw(dc, Pose, centre, centre, UnitPx / dpi);
            }
        }

        private readonly Visual _visual = new Visual();
        private IntPtr _hwnd;
        private bool _shown;

        public MerchantWindow(double unitPx)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            Left = -3000; Top = -3000; Width = 100; Height = 100;
            Title = "Aladdin Rug merchant";
            _visual.UnitPx = unitPx;
            _visual.SizePx = (int)(Math.Ceiling(110 * unitPx));
            Content = _visual;
            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                long ex = Native.GetExStyle(_hwnd);
                Native.SetExStyle(_hwnd, (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT) & ~Native.WS_EX_APPWINDOW);
            };
            Show();
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
        }

        /// <summary>Put him at (x, y) in screen pixels, in this pose, directly above <paramref name="rug"/> in the stacking order.</summary>
        public void Present(double x, double y, MerchantPose pose, IntPtr rug)
        {
            if (_hwnd == IntPtr.Zero) return;
            _visual.Pose = pose;
            int s = _visual.SizePx;

            IntPtr prev = Native.GetWindow(rug, Native.GW_HWNDPREV);
            bool placed = prev == _hwnd;
            uint flags = Native.SWP_NOACTIVATE | (_shown ? 0 : Native.SWP_SHOWWINDOW) | (placed ? Native.SWP_NOZORDER : 0);
            Native.SetWindowPos(_hwnd, placed ? IntPtr.Zero : (prev == IntPtr.Zero ? Native.HWND_TOP : prev), (int)Math.Round(x - s / 2.0), (int)Math.Round(y - s / 2.0), s, s, flags);
            _shown = true;
            _visual.InvalidateVisual();
        }

        public void Vanish()
        {
            if (_hwnd != IntPtr.Zero) Native.ShowWindow(_hwnd, Native.SW_HIDE);
            _shown = false;
        }
    }
}
