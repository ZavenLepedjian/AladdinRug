using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace AladdinRug
{
    /// <summary>Small synthetic surface checks: no windows, desktop access, or saved rug settings.</summary>
    internal static class CollisionChecks
    {
        public static int Run(string path)
        {
            using var log = new StreamWriter(path);
            int failed = 0;
            void Test(string name, Action action)
            {
                try { action(); log.WriteLine("PASS " + name); }
                catch (Exception error) { failed++; log.WriteLine("FAIL " + name + ": " + error.Message); }
                log.Flush();
            }
            Test("moving vertex against face", VertexFaceMotion);
            Test("both moving faces cannot exchange sides", BothMovingFaces);
            Test("edge crossing without an interior vertex", EdgeEdgeMotion);
            Test("coplanar and degenerate surfaces", CoplanarMotion);
            Test("shared translation is unrestricted", CommonTranslation);
            Test("contact can separate and slide", ContactEscape);
            Test("held surfaces still restrict motion", HeldFaces);
            Test("drawn triangles match the physics surface", ExactSurface);
            Test("diagonal fold, release and re-grab", DiagonalRegression);
            Test("wall and floor contact", BoundaryRegression);
            Test("large mesh follows the hand without crossing", LargeClothTracking);
            Test("scripted flatten still reaches the original flat layout", FlattenEndpoint);
            log.WriteLine(failed == 0 ? "All 12 checks passed." : failed + " check(s) failed.");
            return failed == 0 ? 0 : 1;
        }

        private static void Check(bool condition, string message = "Unexpected surface result")
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Surface
        {
            public readonly float[] X, Y, Z, Mass;
            public readonly ClothCollision Collision;
            public Surface(params Vector3[] points)
            {
                X = points.Select(p => p.X).ToArray(); Y = points.Select(p => p.Y).ToArray(); Z = points.Select(p => p.Z).ToArray();
                Mass = Enumerable.Repeat(1f, points.Length).ToArray();
                Collision = new ClothCollision(points.Length, Enumerable.Range(0, points.Length).ToArray());
                Collision.Begin(X, Y, Z);
            }
            public float Accept() => Collision.LimitMotion(X, Y, Z);
        }
        private static Surface ParallelFaces() => new Surface(new(0, 0, 0), new(10, 0, 0), new(0, 10, 0), new(2, 2, 10), new(4, 2, 10), new(2, 4, 10));
        private static void VertexFaceMotion()
        {
            var s = ParallelFaces(); for (int i = 3; i < 6; i++) s.Z[i] = -10;
            float f = s.Accept(); Check(f > .45f && f < .5f, "Incorrect impact time");
            Check(s.Z.Skip(3).Min() >= ClothCollision.Clearance && s.Z.Take(3).All(z => z == 0));
        }
        private static void BothMovingFaces()
        {
            var s = ParallelFaces(); for (int i = 0; i < 6; i++) s.Z[i] = i < 3 ? 10 : 0;
            Check(s.Accept() < .5f);
            Check(s.Z[3] - s.Z[0] >= ClothCollision.Clearance, "Previous layer order was exchanged");
        }
        private static void EdgeEdgeMotion()
        {
            var s = new Surface(new(-5, 0, 0), new(5, 0, 0), new(-5, -.2f, 0), new(0, -5, 5), new(0, 5, 5), new(.2f, -5, 5));
            for (int i = 3; i < 6; i++) s.Z[i] = -5;
            Check(s.Accept() < .5f && s.Z[3] >= ClothCollision.Clearance);
        }
        private static void CoplanarMotion()
        {
            var s = new Surface(new(0, 0, 0), new(10, 0, 0), new(0, 10, 0), new(15, 2, 0), new(17, 2, 0), new(15, 4, 0));
            for (int i = 3; i < 6; i++) s.X[i] -= 13;
            Check(s.Accept() < 1 && s.X[3] + s.Y[3] > 10, "Coplanar entry was accepted");
            var d = new Surface(new(-5, 0, 0), new(0, 0, 0), new(5, 0, 0), new(0, -5, 5), new(0, 0, 5), new(0, 5, 5));
            for (int i = 3; i < 6; i++) d.Z[i] = -5;
            Check(d.Accept() < .5f && d.Z.All(float.IsFinite) && d.Z[3] >= ClothCollision.Clearance);
        }
        private static void CommonTranslation()
        {
            var s = ParallelFaces(); for (int i = 0; i < 6; i++) { s.X[i] += 1000; s.Y[i] += 300; s.Z[i] += 80; }
            Check(s.Accept() == 1 && s.X[0] == 1000, "Rigid travel was unnecessarily blocked");
        }
        private static void ContactEscape()
        {
            foreach (bool slide in new[] { false, true })
            {
                var s = ParallelFaces(); for (int i = 3; i < 6; i++) s.Z[i] = ClothCollision.Clearance;
                s.Collision.Begin(s.X, s.Y, s.Z);
                for (int i = 3; i < 6; i++) { if (slide) s.X[i] += 8; else s.Z[i] = 5; }
                Check(s.Accept() == 1, "An existing contact cannot escape");
                Check(s.Collision.Intersections(s.X, s.Y, s.Z) == 0);
            }
        }

        private static void HeldFaces()
        {
            var s = ParallelFaces(); Array.Clear(s.Mass); for (int i = 3; i < 6; i++) s.Z[i] = -.2f;
            s.Collision.Project(s.X, s.Y, s.Z, s.Mass, Cloth.Layer);
            Check(s.Accept() < 1 && s.Z[3] >= ClothCollision.Clearance);
        }
        private static void ExactSurface()
        {
            var c = new Cloth(30, 30, 60, 60, 160, 160, 20);
            for (int i = 0; i < c.Count; i++) c.Z[i] = 10 + (i % 5) * 11;
            byte[] tex = new byte[4 * 4 * 4]; Array.Fill(tex, (byte)255);
            var renderer = new ClothRenderer(c, 160, 160, 1, tex, tex, 4, 4, 0);
            renderer.Render(new byte[160 * 160 * 4]);
            int dx = 2 * (c.Nx - 1) + 1, dy = 2 * (c.Ny - 1) + 1;
            foreach (var (field, values) in new[] { ("_vx", c.X), ("_vy", c.Y), ("_vz", c.Z) })
            {
                var actual = (float[])typeof(ClothRenderer).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(renderer);
                for (int y = 0; y < dy; y++) for (int x = 0; x < dx; x++)
                {
                    int k = (y / 2) * c.Nx + x / 2;
                    float expected = (x % 2, y % 2) switch { (0, 0) => values[k], (1, 0) => (values[k] + values[k + 1]) / 2, (0, 1) => (values[k] + values[k + c.Nx]) / 2, _ => (values[k + 1] + values[k + c.Nx]) / 2 };
                    Check(Math.Abs(actual[y * dx + x] - expected) < .0001f, "Renderer left the collision surface");
                }
            }
        }

        // Independent segment/plane/barycentric oracle. It never calls the collision implementation.
        // Strict crossings require endpoints on opposite sides by 0.02 DIP; separate fixtures cover coplanarity and CCD.
        private readonly record struct GeomTriangle(int A, int B, int C, Vector3 P, Vector3 Q, Vector3 R)
        {
            public Vector3 Min => Vector3.Min(P, Vector3.Min(Q, R));
            public Vector3 Max => Vector3.Max(P, Vector3.Max(Q, R));
            public bool Shares(GeomTriangle t) => A == t.A || A == t.B || A == t.C || B == t.A || B == t.B || B == t.C || C == t.A || C == t.B || C == t.C;
        }
        private static bool StrictCross(Vector3 p, Vector3 q, GeomTriangle t)
        {
            Vector3 e1 = t.Q - t.P, e2 = t.R - t.P, n = Vector3.Cross(e1, e2);
            double length = n.Length(); if (length < 1e-7) return false;
            double a = Vector3.Dot(p - t.P, n) / length, b = Vector3.Dot(q - t.P, n) / length;
            if (!((a < -.02 && b > .02) || (a > .02 && b < -.02))) return false;
            Vector3 hit = p + (q - p) * (float)(a / (a - b)), v = hit - t.P;
            double aa = Vector3.Dot(e1, e1), bb = Vector3.Dot(e2, e2), ab = Vector3.Dot(e1, e2), av = Vector3.Dot(e1, v), bv = Vector3.Dot(e2, v);
            double denominator = aa * bb - ab * ab; if (Math.Abs(denominator) < 1e-10) return false;
            double u = (bb * av - ab * bv) / denominator, w = (aa * bv - ab * av) / denominator;
            return u > 1e-6 && w > 1e-6 && u + w < 1 - 1e-6;
        }
        private static bool IndependentCrossing(Cloth c)
        {
            var tris = new GeomTriangle[2 * (c.Nx - 1) * (c.Ny - 1)]; int n = 0;
            Vector3 At(int k) => new(c.X[k], c.Y[k], c.Z[k]);
            void Add(int a, int b, int d) => tris[n++] = new(a, b, d, At(a), At(b), At(d));
            for (int y = 0; y < c.Ny - 1; y++) for (int x = 0; x < c.Nx - 1; x++) { int k = y * c.Nx + x; Add(k, k + 1, k + c.Nx); Add(k + 1, k + c.Nx + 1, k + c.Nx); }
            Array.Sort(tris, (a, b) => a.Min.X.CompareTo(b.Min.X));
            for (int i = 0; i < tris.Length; i++) for (int j = i + 1; j < tris.Length && tris[j].Min.X <= tris[i].Max.X; j++)
            {
                var a = tris[i]; var b = tris[j]; if (a.Shares(b)) continue;
                Vector3 lo = Vector3.Max(a.Min, b.Min), hi = Vector3.Min(a.Max, b.Max);
                if (lo.Y > hi.Y || lo.Z > hi.Z) continue;
                if (StrictCross(a.P, a.Q, b) || StrictCross(a.Q, a.R, b) || StrictCross(a.R, a.P, b) || StrictCross(b.P, b.Q, a) || StrictCross(b.Q, b.R, a) || StrictCross(b.R, b.P, a)) return true;
            }
            return false;
        }
        private static void DiagonalRegression()
        {
            var c = new Cloth(200, 160, 708, 470, 1920, 1152, 20);
            int seed = c.Nx - 1;
            float first = c.X[seed];
            Check(c.Grab(908, 160), "Could not grab the original corner");
            for (int frame = 0; frame < 220; frame++)
            {
                if (frame < 140)
                {
                    float t = Math.Min(1, frame / 80f);
                    c.MoveHand(908 - 500 * t, 160 + 330 * t);
                }
                else if (frame == 140) c.Release();
                c.Step(1f / 60);
                Check(!IndependentCrossing(c), "Crossing at frame " + frame);
                Check(c.X.All(float.IsFinite) && c.Y.All(float.IsFinite) && c.Z.All(z => float.IsFinite(z) && z >= Cloth.Floor - .001f));
                if (frame == 100) Check(first - c.X[seed] > 300 && c.HandLag < 100, "The collision guard stalled dragging");
            }
            Check(c.Grab(c.X[seed], c.Y[seed]), "Could not grab the folded rug again");
            float start = c.X[seed], top = c.Y[seed];
            float[] before = (float[])c.X.Clone();
            for (int frame = 0; frame < 60; frame++)
            {
                c.MoveHand(start + frame * 2, top - frame);
                c.Step(1f / 60);
                Check(!IndependentCrossing(c), "Crossing after re-grab");
            }
            // Grab picks the topmost layer; the original material corner may now be underneath it.
            Check(c.X.Where((x, i) => x - before[i] > 30).Any() && c.HandLag < 30, "Re-grab did not move the selected layer");
        }

        private static void BoundaryRegression()
        {
            var c = new Cloth(40, 40, 240, 180, 400, 300, 20);
            Check(c.Grab(280, 40));
            for (int frame = 0; frame < 100; frame++)
            {
                c.MoveHand(280 + frame * 3, 40 - frame * 2);
                c.Step(1f / 60);
                Check(!IndependentCrossing(c), "Crossing at a room boundary");
                // Held points follow the mouse outside the room, as in the original hand behaviour.
                Check(c.X.All(float.IsFinite) && c.Y.All(float.IsFinite) && c.Z.All(z => z >= Cloth.Floor - .001f));
            }
            c.Release();
            for (int frame = 0; frame < 60; frame++)
            {
                c.Step(1f / 60);
                Check(!IndependentCrossing(c), "Crossing after release at a room boundary");
            }
            Check(c.X.All(x => x >= 0 && x <= 400) && c.Y.All(y => y >= 0 && y <= 300), "Released cloth remained outside the room");
        }

        private static void LargeClothTracking()
        {
            var c = new Cloth(200, 160, 1476, 934, 3840, 2304, 20);
            int seed = c.Nx - 1;
            float start = c.X[seed];
            Check(c.Grab(start, 160));
            for (int frame = 0; frame < 150; frame++)
            {
                if (frame < 110)
                {
                    float t = Math.Min(1, frame / 80f);
                    c.MoveHand(start - 1476 * .7f * t, 160 + 934 * .7f * t);
                }
                else if (frame == 110) c.Release();
                c.Step(1f / 60);
                if (frame % 10 == 0 || frame == 149) Check(!IndependentCrossing(c), "Large cloth crossed at frame " + frame);
                if (frame == 100)
                    Check(c.HandLag < 100 && start - c.X[seed] > 900 && c.MaxStretch < 1.1f, "Large cloth stopped following the hand");
            }
        }

        private static void FlattenEndpoint()
        {
            var c = new Cloth(40, 40, 240, 180, 400, 300, 20);
            c.Grab(280, 40);
            for (int frame = 0; frame < 40; frame++) { c.MoveHand(280 - frame * 2, 40 + frame); c.Step(1f / 60); }
            c.BeginFlatten(0, 0, 400, 300);
            for (int frame = 1; frame <= 30; frame++) c.Flatten(frame / 30f);
            Check(!c.IsHeld && c.Z.All(z => Math.Abs(z - Cloth.Floor) < .001f));
            for (int y = 0; y < c.Ny; y++) for (int x = 0; x < c.Nx; x++)
            {
                int i = y * c.Nx + x;
                Check(Math.Abs(c.X[i] - c.X[0] - x * c.Sx) < .001f && Math.Abs(c.Y[i] - c.Y[0] - y * c.Sy) < .001f);
            }
            // Flatten is the original scripted transition, not part of the drag collision guarantee.
        }
    }
}
