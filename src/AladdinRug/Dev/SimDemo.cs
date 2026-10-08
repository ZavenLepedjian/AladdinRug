using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AladdinRug
{
    /// <summary>
    /// Runs the cloth rug through scripted "hand" moves with no window at all and saves pictures of the result:
    /// AladdinRug.exe --simtest outdir
    /// </summary>
    internal static class SimDemo
    {
        public static void Run(string dir, string only)
        {
            Directory.CreateDirectory(dir);
            DeskGeometry geo = DeskGeometry.Primary();
            double s = geo.Scale;
            StyleSpec spec = RugStyles.Get(RugStyles.Current);
            int rugW = (int)(geo.W * RugArt.DefaultWidthFraction * spec.WidthFactor), rugH = (int)(geo.H * RugArt.DefaultHeightFraction * spec.HeightFactor);
            RugArt.Images art = RugArt.RenderAll(rugW, rugH, s, s);

            float roomW = (float)(geo.W / s), roomH = (float)(geo.H / s);
            float rugWd = (float)(rugW / s), rugHd = (float)(rugH / s), pad = (float)RugArt.PadDip;
            float meshW = rugWd - 2 * pad, meshH = rugHd - 2 * pad;
            float left = (roomW - rugWd) / 2 + pad, top = (roomH - rugHd) / 2 + pad;
            float cx = left + meshW / 2, cy = top + meshH / 2;

            var scenarios = new Dictionary<string, Action<Cloth, int>>();

            // pinch the middle, lift it, drag it right, let go
            scenarios["middle"] = (c, f) =>
            {
                if (f == 0) c.Grab(cx - 120, cy);
                if (f >= 1 && f <= 70) c.MoveHand(cx - 120 + 320 * f / 70f, cy + 20 * f / 70f);
                if (f == 90) c.Release();
            };

            // pinch just the middle, lift and hold it still
            scenarios["tent"] = (c, f) =>
            {
                if (f == 0) c.Grab(cx - 100, cy - 40);
                if (f == 100) c.Release();
            };

            // lift a corner and fold it over the rug
            scenarios["corner"] = (c, f) =>
            {
                if (f == 0) c.Grab(left + meshW - 14, top + meshH - 14);
                if (f >= 1 && f <= 90) c.MoveHand(left + meshW - 14 - (meshW * 0.55f) * f / 90f, top + meshH - 14 - (meshH * 0.55f) * f / 90f);
                if (f == 130) c.Release();
            };

            // lift the right edge and pull it back over the middle
            scenarios["edge"] = (c, f) =>
            {
                if (f == 0) c.Grab(left + meshW - 10, cy);
                if (f >= 1 && f <= 100) c.MoveHand(left + meshW - 10 - meshW * 0.60f * f / 100f, cy);
                if (f == 150) c.Release();
            };

            // drag the whole rug by its edge
            scenarios["drag"] = (c, f) =>
            {
                if (f == 0) c.Grab(left + 10, cy);
                if (f >= 1 && f <= 120) c.MoveHand(left + 10 + 260 * f / 120f, cy - 80 * f / 120f);
                if (f == 150) c.Release();
            };

            var snaps = new Dictionary<string, int[]>
            {
                ["middle"] = new[] { 45, 90, 300 },
                ["tent"] = new[] { 100, 260 },
                ["corner"] = new[] { 60, 130, 330 },
                ["edge"] = new[] { 70, 150, 350 },
                ["drag"] = new[] { 80, 150, 330 },
            };

            foreach (var kv in scenarios)
            {
                if (only != null && only != kv.Key) continue;
                var cloth = new Cloth(left, top, meshW, meshH, roomW, roomH, 20f, spec.Feel);
                var renderer = new ClothRenderer(cloth, geo.W, geo.H, (float)s, art.FacePx, art.BackPx, art.TexW, art.TexH, (float)(pad * s));
                var buffer = new byte[geo.W * geo.H * 4];
                int last = snaps[kv.Key][snaps[kv.Key].Length - 1];
                var watch = System.Diagnostics.Stopwatch.StartNew();
                long renderTicks = 0, simTicks = 0;
                for (int f = 0; f <= last; f++)
                {
                    kv.Value(cloth, f);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    cloth.Step(1f / 60f);
                    simTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    if (Array.IndexOf(snaps[kv.Key], f) >= 0)
                    {
                        long r0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        renderer.Render(buffer);
                        renderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - r0;
                        Save(Path.Combine(dir, kv.Key + "_" + f + ".png"), buffer, geo.W, geo.H, (float)s);
                    }
                }
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Console.WriteLine("{0}: {1} points, sim {2:F2} ms/step avg, last render {3:F1} ms, speed at end {4:F3}",
                    kv.Key, cloth.Count, simTicks * ms / (last + 1), renderTicks * ms / snaps[kv.Key].Length, cloth.Speed);
            }
        }

        /// <summary>Lift the rug's edge, let go, and watch the dust: a contact sheet of the frames after it lands.</summary>
        public static void DustDemo(string dir)
        {
            Directory.CreateDirectory(dir);
            DeskGeometry geo = DeskGeometry.Primary();
            double s = geo.Scale;
            StyleSpec spec = RugStyles.Get(RugStyles.Current);
            int rugW = (int)(geo.W * RugArt.DefaultWidthFraction * spec.WidthFactor), rugH = (int)(geo.H * RugArt.DefaultHeightFraction * spec.HeightFactor);
            RugArt.Images art = RugArt.RenderAll(rugW, rugH, s, s);
            float roomW = (float)(geo.W / s), roomH = (float)(geo.H / s);
            float rugWd = (float)(rugW / s), rugHd = (float)(rugH / s), pad = (float)RugArt.PadDip;
            float meshW = rugWd - 2 * pad, meshH = rugHd - 2 * pad;
            float left = (roomW - rugWd) / 2 + pad, top = (roomH - rugHd) / 2 + pad;
            float cy = top + meshH / 2;

            var cloth = new Cloth(left, top, meshW, meshH, roomW, roomH, 20f, spec.Feel);
            var renderer = new ClothRenderer(cloth, geo.W, geo.H, (float)s, art.FacePx, art.BackPx, art.TexW, art.TexH, (float)(pad * s));
            var dust = new Dust(1f);
            var view = new DustView();
            var buffer = new byte[geo.W * geo.H * 4];
            var frames = new System.Collections.Generic.List<byte[]>();
            cloth.Grab(left + meshW - 10, cy);

            int releaseAt = 70, impacts = 0, hardest = 0, peakDust = 0;
            float strongest = 0;
            int[] shots = { releaseAt - 2, releaseAt + 8, releaseAt + 14, releaseAt + 22, releaseAt + 34, releaseAt + 50, releaseAt + 70, releaseAt + 100 };
            for (int f = 0; f <= releaseAt + 110; f++)
            {
                if (f >= 1 && f < releaseAt) cloth.MoveHand(left + meshW - 10 - meshW * 0.45f * Math.Min(1f, f / 50f), cy);
                if (f == releaseAt) cloth.Release();
                cloth.Step(1f / 60f);
                impacts += cloth.ImpactCount;
                hardest = Math.Max(hardest, cloth.ImpactCount);
                for (int k = 0; k < cloth.ImpactCount; k++) strongest = Math.Max(strongest, cloth.ImpP[k]);
                dust.FromCloth(cloth);
                dust.Step(1f / 60f);
                dust.CopyTo(view);
                peakDust = Math.Max(peakDust, view.Count);
                if (Array.IndexOf(shots, f) >= 0)
                {
                    renderer.Render(buffer, view);
                    frames.Add(OnDesktop(buffer, geo.W, geo.H, (float)s));
                    {
                        int cw = rugW + 300, ch = rugH + 240, cx0 = Math.Max(0, (geo.W - cw) / 2), cy0 = Math.Max(0, (geo.H - ch) / 2);
                        BitmapSource full = BitmapSource.Create(geo.W, geo.H, 96, 96, PixelFormats.Pbgra32, null, frames[frames.Count - 1], geo.W * 4);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(new CroppedBitmap(full, new System.Windows.Int32Rect(cx0, cy0, Math.Min(cw, geo.W - cx0), Math.Min(ch, geo.H - cy0)))));
                        using (FileStream fs = File.Create(Path.Combine(dir, "crop_" + spec.Name + "_" + f + ".png"))) enc.Save(fs);
                    }
                    Console.WriteLine("frame {0}: {1} puffs in the air", f, view.Count);
                }
            }
            Console.WriteLine("edge hits: {0} points in all, {1} in one step, strongest {2:F2}; most puffs at once {3}", impacts, hardest, strongest, peakDust);
            Montage(Path.Combine(dir, "dust_" + spec.Name + ".png"), frames, geo.W, geo.H, 4, 0.30);
        }

        /// <summary>A row of merchant poses on a rug-coloured floor, to judge how he looks.</summary>
        public static void MerchantSheet(string path)
        {
            var poses = new MerchantPose[]
            {
                new MerchantPose { Heading = 180, Phase = 0.0, Walk = 1 },
                new MerchantPose { Heading = 180, Phase = 1.6, Walk = 1 },
                new MerchantPose { Heading = 180, Lean = 1, Crouch = 0.5, Reach = 1, Walk = 0.4, Phase = 1 },
                new MerchantPose { Heading = 180, Lean = 1, Crouch = 0.95, Reach = 1 },
                new MerchantPose { Heading = 0, Lean = 1, Crouch = 0.5, Reach = 1, Walk = 0.5, Phase = 2 },
                new MerchantPose { Heading = 0, Reach = 0.85 },
                new MerchantPose { Heading = 90, Phase = 0.8, Walk = 1 },
                new MerchantPose { Heading = 20, Phase = 2.4, Walk = 1, Reach = 0.8, Lean = 0.35, Carry = 4, CarryHue = 20 },
                new MerchantPose { Heading = 200, Phase = 1.0, Walk = 1, Reach = 0.9, Lean = 0.55, Broom = 1, BroomSwing = 0.7 },
            };
            double k = 3.6, cell = 210;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x7B, 0x1C, 0x27)), null, new System.Windows.Rect(0, 0, cell * poses.Length, cell));
                for (int i = 0; i < poses.Length; i++) MerchantArt.Draw(dc, poses[i], cell * i + cell / 2, cell / 2, k);
            }
            var bmp = new RenderTargetBitmap((int)(cell * poses.Length), (int)cell, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(path)) enc.Save(fs);
        }

        private static void Save(string path, byte[] rug, int w, int h, float s)
        {
            byte[] o = OnDesktop(rug, w, h, s);
            BitmapSource full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, o, w * 4);
            var small = new TransformedBitmap(full, new ScaleTransform(0.55, 0.55));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(small));
            using (FileStream fs = File.Create(path)) enc.Save(fs);
        }

        // Put the rug over a made-up desktop (dark wallpaper and a few "icons").
        private static byte[] OnDesktop(byte[] rug, int w, int h, float s)
        {
            var o = new byte[rug.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    float bgB = 92 + 30f * y / h, bgG = 74 + 20f * y / h, bgR = 58;
                    // little icon squares on a grid, like a cluttered desktop
                    int gx = (int)(x / (s * 76)), gy = (int)(y / (s * 86));
                    float fx = x / (s * 76) - gx, fy = y / (s * 86) - gy;
                    if (fx > 0.30f && fx < 0.70f && fy > 0.10f && fy < 0.50f) { bgB = 235; bgG = 235; bgR = 225; }
                    float keep = 1f - rug[i + 3] / 255f;
                    o[i] = (byte)Math.Min(255, rug[i] + bgB * keep);
                    o[i + 1] = (byte)Math.Min(255, rug[i + 1] + bgG * keep);
                    o[i + 2] = (byte)Math.Min(255, rug[i + 2] + bgR * keep);
                    o[i + 3] = 255;
                }
            return o;
        }

        // Many frames on one sheet, each shrunk, so a whole gesture can be judged at a glance.
        private static void Montage(string path, System.Collections.Generic.List<byte[]> frames, int w, int h, int cols, double scale)
        {
            int cw = (int)(w * scale), ch = (int)(h * scale), rows = (frames.Count + cols - 1) / cols;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                for (int n = 0; n < frames.Count; n++)
                {
                    BitmapSource full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, frames[n], w * 4);
                    var small = new TransformedBitmap(full, new ScaleTransform(scale, scale));
                    dc.DrawImage(small, new System.Windows.Rect((n % cols) * (cw + 4), (n / cols) * (ch + 4), cw, ch));
                }
            }
            var bmp = new RenderTargetBitmap(cols * (cw + 4), rows * (ch + 4), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(path)) enc.Save(fs);
        }

        // Pixels that should be solid rug but aren't: transparent with solid pixels on all four sides.
        private static int CountPinholes(byte[] buf, int w, int h)
        {
            int holes = 0;
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = (y * w + x) * 4;
                    if (buf[i + 3] > 40) continue;
                    if (buf[i - 4 + 3] > 250 && buf[i + 4 + 3] > 250 && buf[i - w * 4 + 3] > 250 && buf[i + w * 4 + 3] > 250) holes++;
                }
            return holes;
        }


        /// <summary>How long a frame takes while the rug is being dragged: physics step plus drawing.</summary>
        public static void Bench()
        {
            DeskGeometry geo = DeskGeometry.Primary();
            double s = geo.Scale;
            StyleSpec spec = RugStyles.Get(RugStyles.Current);
            int rugW = (int)(geo.W * RugArt.DefaultWidthFraction * spec.WidthFactor), rugH = (int)(geo.H * RugArt.DefaultHeightFraction * spec.HeightFactor);
            RugArt.Images art = RugArt.RenderAll(rugW, rugH, s, s);
            float roomW = (float)(geo.W / s), roomH = (float)(geo.H / s);
            float rugWd = (float)(rugW / s), rugHd = (float)(rugH / s), pad = (float)RugArt.PadDip;
            float meshW = rugWd - 2 * pad, meshH = rugHd - 2 * pad;
            float left = (roomW - rugWd) / 2 + pad, top = (roomH - rugHd) / 2 + pad;
            var cloth = new Cloth(left, top, meshW, meshH, roomW, roomH, 20f, spec.Feel);
            var renderer = new ClothRenderer(cloth, geo.W, geo.H, (float)s, art.FacePx, art.BackPx, art.TexW, art.TexH, (float)(pad * s));
            var buffer = new byte[geo.W * geo.H * 4];
            cloth.Grab(left + meshW / 2, top + meshH / 2);
            double sim = 0, draw = 0;
            int frames = 120;
            for (int f = 0; f < frames + 20; f++)
            {
                cloth.MoveHand(left + meshW / 2 + 260 * (float)Math.Sin(f * 0.05), top + meshH / 2 + 120 * (float)Math.Cos(f * 0.05));
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                cloth.Step(1f / 60f);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                renderer.Render(buffer);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (f >= 20) { sim += t1 - t0; draw += t2 - t1; }
            }
            Console.WriteLine("  physics breakdown per step (ms): move {0:F2}, grid {1:F2}, springs {2:F2}, stacking {3:F2}, settle {4:F2}",
                Cloth.Profile[0] / (frames + 20), Cloth.Profile[1] / (frames + 20), Cloth.Profile[2] / (frames + 20), Cloth.Profile[3] / (frames + 20), Cloth.Profile[4] / (frames + 20));
            Console.WriteLine("  drawing breakdown per frame (ms): smooth {0:F2}, clear {1:F2}, sort {2:F2}, pixels {3:F2}, shadows {4:F2}",
                ClothRenderer.Profile[0] / (frames + 20), ClothRenderer.Profile[1] / (frames + 20), ClothRenderer.Profile[2] / (frames + 20), ClothRenderer.Profile[3] / (frames + 20), ClothRenderer.Profile[4] / (frames + 20));
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine("per frame while dragging: physics {0:F1} ms + drawing {1:F1} ms = {2:F1} ms  ({3:F0} frames/second possible)",
                sim * ms / frames, draw * ms / frames, (sim + draw) * ms / frames, 1000.0 / ((sim + draw) * ms / frames));
        }

        /// <summary>Wild gestures, judged on contact sheets, plus a count of cracks in the drawing.</summary>
        public static void Stress(string dir, string only)
        {
            Directory.CreateDirectory(dir);
            DeskGeometry geo = DeskGeometry.Primary();
            double s = geo.Scale;
            StyleSpec spec = RugStyles.Get(RugStyles.Current);
            int rugW = (int)(geo.W * RugArt.DefaultWidthFraction * spec.WidthFactor), rugH = (int)(geo.H * RugArt.DefaultHeightFraction * spec.HeightFactor);
            RugArt.Images art = RugArt.RenderAll(rugW, rugH, s, s);

            // an all-opaque white picture: any hole in this is a drawing bug, not part of the rug's design
            var white = new byte[art.FacePx.Length];
            for (int i = 0; i < white.Length; i++) white[i] = 255;

            float roomW = (float)(geo.W / s), roomH = (float)(geo.H / s);
            float rugWd = (float)(rugW / s), rugHd = (float)(rugH / s), pad = (float)RugArt.PadDip;
            float meshW = rugWd - 2 * pad, meshH = rugHd - 2 * pad;
            float left = (roomW - rugWd) / 2 + pad, top = (roomH - rugHd) / 2 + pad;
            float cx = left + meshW / 2, cy = top + meshH / 2;

            // each gesture: where the hand starts, and where it is on each frame (null = let go)
            var gestures = new System.Collections.Generic.Dictionary<string, Func<int, (float x, float y)?>>();
            var starts = new System.Collections.Generic.Dictionary<string, (float x, float y)>();
            gestures["circles"] = f => f < 150 ? ((float x, float y)?)(cx - 80 + 200 * (float)Math.Cos(f * 0.09), cy + 140 * (float)Math.Sin(f * 0.09)) : null;
            starts["circles"] = (cx - 80 + 200, cy);
            gestures["flick"] = f => f < 40 ? ((float x, float y)?)(left + meshW - 20 - 700f * f / 40f, top + meshH - 20 - 250f * f / 40f) : null;
            starts["flick"] = (left + meshW - 20, top + meshH - 20);
            gestures["shake"] = f => f < 120 ? ((float x, float y)?)(cx + 160 * (float)Math.Sin(f * 0.35), cy + 40 + 40 * (float)Math.Cos(f * 0.2)) : null;
            starts["shake"] = (cx, cy + 80);
            gestures["across"] = f => f < 90 ? ((float x, float y)?)(left + 30 + (meshW - 60) * f / 90f, top + 40 + (meshH - 80) * f / 90f) : null;
            starts["across"] = (left + 30, top + 40);

            foreach (var kv in gestures)
            {
                if (only != null && only != kv.Key) continue;
                foreach (bool solid in new[] { true, false })
                {
                    var cloth = new Cloth(left, top, meshW, meshH, roomW, roomH, 20f, spec.Feel);
                    var renderer = new ClothRenderer(cloth, geo.W, geo.H, (float)s, solid ? white : art.FacePx, solid ? white : art.BackPx, art.TexW, art.TexH, (float)(pad * s));
                    var buffer = new byte[geo.W * geo.H * 4];
                    var frames = new System.Collections.Generic.List<byte[]>();
                    int worstHoles = 0, holeFrames = 0, grabbed = 0, framesChecked = 0, peakPen = 0;
                    float peakLag = 0, sumLag = 0; int lagN = 0; float peakBack = 0, peakShear95 = 0, peakShearWorst = 0, peakStretch95 = 0, peakStretchWorst = 0;
                    for (int f = 0; f < 330; f++)
                    {
                        if (f == 0) grabbed = cloth.Grab(starts[kv.Key].x, starts[kv.Key].y) ? 1 : 0;
                        var hand = kv.Value(f);
                        if (hand.HasValue) cloth.MoveHand(hand.Value.x, hand.Value.y);
                        else if (cloth.IsHeld) cloth.Release();
                        cloth.Step(1f / 60f);
                        if (!solid && cloth.IsHeld) { peakLag = Math.Max(peakLag, cloth.HandLag); sumLag += cloth.HandLag; lagN++; }

                        if (f % 6 == 0)
                        {
                            renderer.Render(buffer);
                            framesChecked++;
                            if (solid)
                            {
                                int holes = CountPinholes(buffer, geo.W, geo.H);
                                if (holes > 0) holeFrames++;
                                worstHoles = Math.Max(worstHoles, holes);
                            }
                            else if (f % 18 == 0 && frames.Count < 16) frames.Add(OnDesktop(buffer, geo.W, geo.H, (float)s));
                            if (!solid && (f == 36 || f == 60 || f == 96)) Save(Path.Combine(dir, "frame_" + kv.Key + "_" + f + ".png"), buffer, geo.W, geo.H, (float)s);
                            peakBack = Math.Max(peakBack, renderer.BackFacingShare);
                            peakPen = Math.Max(peakPen, cloth.Penetrations());
                            cloth.ShearStats(out float s95, out float sw);
                            cloth.StretchStats(out float st95, out float stw);
                            peakStretch95 = Math.Max(peakStretch95, st95); peakStretchWorst = Math.Max(peakStretchWorst, stw);
                            peakShear95 = Math.Max(peakShear95, s95); peakShearWorst = Math.Max(peakShearWorst, sw);
                        }
                    }
                    if (solid) Console.WriteLine("{0,-8} cracks: worst frame {1} pinholes, {2} of {3} frames affected; grabbed={4}", kv.Key, worstHoles, holeFrames, framesChecked, grabbed);
                    else
                    {
                        Montage(Path.Combine(dir, "stress_" + kv.Key + ".png"), frames, geo.W, geo.H, 4, 0.26);
                        Console.WriteLine("{0,-8} sheet saved; underside {1:P0}; layers-inside-each-other {2}; squareness lost: 95% within {3:F0} deg, worst {4:F0}; stretch: 95% under +{5:P0}, worst +{6:P0}; hand behind mouse by {7:F0} DIP on average, {8:F0} at most", kv.Key, peakBack, peakPen, peakShear95, peakShearWorst, peakStretch95 - 1, peakStretchWorst - 1, lagN == 0 ? 0 : sumLag / lagN, peakLag);
                        Save(Path.Combine(dir, "final_" + kv.Key + ".png"), buffer, geo.W, geo.H, (float)s);
                    }
                }
            }
        }

    }
}
