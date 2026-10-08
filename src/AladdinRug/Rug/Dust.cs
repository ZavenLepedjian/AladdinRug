using System;
using System.Windows;

namespace AladdinRug
{
    /// <summary>Where each puff of dust is right now, ready to be painted: position, radius and opacity of each.</summary>
    internal sealed class DustView
    {
        public readonly float[] X = new float[Dust.Max], Y = new float[Dust.Max], R = new float[Dust.Max], A = new float[Dust.Max];
        public int Count;
    }

    /// <summary>
    /// The dust a rug kicks up when it slaps down: soft grey-beige puffs that start at the edges, billow outwards,
    /// slow down and fade. Positions are in "units" (the cloth's DIPs, or pixels for a flat rug); sizes scale with unitsPerDip.
    /// </summary>
    internal sealed unsafe class Dust
    {
        public const int Max = 240;

        private readonly object _lock = new object();
        private readonly float[] _x = new float[Max], _y = new float[Max], _vx = new float[Max], _vy = new float[Max];
        private readonly float[] _age = new float[Max], _life = new float[Max], _r0 = new float[Max], _r1 = new float[Max], _a0 = new float[Max];
        private int _n;
        private volatile bool _active;
        private readonly Random _rnd = new Random(7);
        private readonly float _unit;

        public Dust(float unitsPerDip) { _unit = unitsPerDip; }

        public bool Active => _active;

        public void Clear()
        {
            lock (_lock) { _n = 0; _active = false; }
        }

        /// <summary>A puff leaving (x, y) in the direction (dx, dy). <paramref name="chance"/> thins out a crowd of calls.</summary>
        public void Puff(float x, float y, float dx, float dy, float power, float chance = 1f)
        {
            lock (_lock)
            {
                if (_n >= Max) return;
                if (chance < 1f && _rnd.NextDouble() > chance) return;
                int i = _n++;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1e-4f) { double a = _rnd.NextDouble() * Math.PI * 2; dx = (float)Math.Cos(a); dy = (float)Math.Sin(a); len = 1; }
                dx /= len; dy /= len;

                float p = Math.Max(0.15f, Math.Min(1f, power));
                float speed = (28f + 52f * (float)_rnd.NextDouble()) * (0.6f + 0.8f * p) * _unit;
                float side = ((float)_rnd.NextDouble() - 0.5f) * 38f * _unit;
                _x[i] = x + dx * 3f * _unit + ((float)_rnd.NextDouble() - 0.5f) * 8f * _unit;
                _y[i] = y + dy * 3f * _unit + ((float)_rnd.NextDouble() - 0.5f) * 8f * _unit;
                _vx[i] = dx * speed - dy * side;
                _vy[i] = dy * speed + dx * side;
                _age[i] = 0;
                _life[i] = 0.8f + 0.9f * (float)_rnd.NextDouble();
                _r0[i] = (11f + 9f * (float)_rnd.NextDouble()) * _unit;
                _r1[i] = _r0[i] * (2.2f + 1.2f * (float)_rnd.NextDouble());
                _a0[i] = (0.26f + 0.20f * (float)_rnd.NextDouble()) * (0.6f + 0.6f * p);
                _active = true;
            }
        }

        /// <summary>Puff out of every spot where the cloth just hit the floor.</summary>
        public void FromCloth(Cloth c)
        {
            for (int k = 0; k < c.ImpactCount; k++)
                for (int n = 0; n < 3; n++)
                    Puff(c.ImpX[k], c.ImpY[k], c.ImpDX[k], c.ImpDY[k], c.ImpP[k], 0.10f + 0.65f * c.ImpP[k]);
        }

        public void Step(float dt)
        {
            lock (_lock)
            {
                float drag = MathF.Exp(-2.4f * dt);
                for (int i = 0; i < _n; i++)
                {
                    _age[i] += dt;
                    if (_age[i] >= _life[i])
                    {
                        int last = --_n;                      // swap the last one into this slot
                        _x[i] = _x[last]; _y[i] = _y[last]; _vx[i] = _vx[last]; _vy[i] = _vy[last];
                        _age[i] = _age[last]; _life[i] = _life[last]; _r0[i] = _r0[last]; _r1[i] = _r1[last]; _a0[i] = _a0[last];
                        i--;
                        continue;
                    }
                    _x[i] += _vx[i] * dt;
                    _y[i] += _vy[i] * dt;
                    _vx[i] *= drag;
                    _vy[i] *= drag;
                }
                _active = _n > 0;
            }
        }

        public void CopyTo(DustView v)
        {
            lock (_lock)
            {
                v.Count = _n;
                for (int i = 0; i < _n; i++)
                {
                    float t = _age[i] / _life[i];
                    float fadeIn = Math.Min(1f, _age[i] / 0.07f);
                    v.X[i] = _x[i]; v.Y[i] = _y[i];
                    v.R[i] = _r0[i] + (_r1[i] - _r0[i]) * MathF.Sqrt(t);
                    v.A[i] = _a0[i] * fadeIn * MathF.Pow(1f - t, 1.6f);
                }
            }
        }

        // ------------------------------------------------------------------ painting

        /// <summary>The area the puffs cover, in pixels (empty if there are none).</summary>
        public static Int32Rect Bounds(DustView v, float scale, int w, int h)
        {
            if (v == null || v.Count == 0) return new Int32Rect(0, 0, 0, 0);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < v.Count; i++)
            {
                float r = v.R[i] * scale + 1f, cx = v.X[i] * scale, cy = v.Y[i] * scale;
                if (cx - r < x0) x0 = cx - r; if (cx + r > x1) x1 = cx + r;
                if (cy - r < y0) y0 = cy - r; if (cy + r > y1) y1 = cy + r;
            }
            int ix0 = Math.Max(0, (int)x0), iy0 = Math.Max(0, (int)y0), ix1 = Math.Min(w, (int)x1 + 1), iy1 = Math.Min(h, (int)y1 + 1);
            return new Int32Rect(ix0, iy0, Math.Max(0, ix1 - ix0), Math.Max(0, iy1 - iy0));
        }

        /// <summary>
        /// Lay the puffs over rows y0..y1 of a premultiplied picture. They never cover the rug: where <paramref name="height"/> is above
        /// zero, or <paramref name="occluder"/>'s alpha is solid, the dust is hidden (it is coming out from underneath).
        /// <paramref name="fadePx"/> fades the dust out near the picture's edge.
        /// </summary>
        public static void Composite(byte* dst, int stride, int w, int h, int y0, int y1, DustView v, float scale, float* height, byte* occluder, int fadePx)
        {
            for (int n = 0; n < v.Count; n++)
            {
                float cx = v.X[n] * scale, cy = v.Y[n] * scale, r = v.R[n] * scale, a0 = v.A[n];
                if (a0 <= 0.002f || cy + r < y0 || cy - r >= y1) continue;
                int ys = Math.Max(y0, (int)(cy - r)), ye = Math.Min(y1 - 1, (int)(cy + r));
                int xs = Math.Max(0, (int)(cx - r)), xe = Math.Min(w - 1, (int)(cx + r));
                float inv = 1f / (r * r);

                for (int y = ys; y <= ye; y++)
                {
                    float dy = y + 0.5f - cy, dy2 = dy * dy;
                    float fadeY = fadePx > 0 ? Math.Min(1f, Math.Min(y, h - 1 - y) / (float)fadePx) : 1f;
                    if (fadeY <= 0) continue;
                    for (int x = xs; x <= xe; x++)
                    {
                        float dx = x + 0.5f - cx;
                        float d2 = (dx * dx + dy2) * inv;
                        if (d2 >= 1f) continue;
                        if (height != null && height[y * w + x] > 0f) continue;
                        if (occluder != null && occluder[(y * w + x) * 4 + 3] > 128) continue;

                        float f = 1f - d2;
                        float a = a0 * f * f * fadeY;
                        if (fadePx > 0) a *= Math.Min(1f, Math.Min(x, w - 1 - x) / (float)fadePx);
                        if (a < 0.002f) continue;

                        byte* o = dst + y * stride + x * 4;
                        float keep = 1f - a;
                        o[0] = (byte)(190f * a + o[0] * keep);
                        o[1] = (byte)(212f * a + o[1] * keep);
                        o[2] = (byte)(226f * a + o[2] * keep);
                        o[3] = (byte)(255f * a + o[3] * keep);
                    }
                }
            }
        }

        /// <summary>Paint puffs into <paramref name="area"/> of a whole picture (single-threaded, for the flat rug).</summary>
        public static void Draw(byte[] picture, int w, int h, Int32Rect area, DustView v, float scale, byte[] occluder, int fadePx)
        {
            if (area.Width <= 0 || area.Height <= 0) return;
            fixed (byte* d = picture)
            fixed (byte* occ = occluder)
                Composite(d, w * 4, w, h, area.Y, area.Y + area.Height, v, scale, null, occluder == null ? null : occ, fadePx);
        }
    }
}
