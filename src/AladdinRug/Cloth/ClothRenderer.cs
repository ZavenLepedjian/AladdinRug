using System;
using System.Threading.Tasks;
using System.Windows;

namespace AladdinRug
{
    /// <summary>
    /// Draws the cloth rug into a pixel buffer. The simulated points are first smoothed into a finer surface (so
    /// folds and edges curve instead of showing straight facets); every cell of that surface is two lit, textured
    /// triangles; folded-over parts show the underside; and shadows are cast from the rug's height, so a lifted
    /// flap darkens the floor and the rug beneath it. Done on the CPU, in parallel, because a transparent window
    /// needs real per-pixel alpha.
    ///
    /// One instance must be used from one thread at a time, but it can draw into any number of output buffers.
    /// </summary>
    internal sealed unsafe class ClothRenderer
    {
        // Light from above and to the upper left.
        private const float Lx = -0.45f, Ly = -0.55f, Lz = 0.70f;
        private const float Ambient = 0.46f, Diffuse = 0.78f;
        private const float FloorShadow = 0.50f, ClothShadow = 0.42f, ShadowSoftness = 3.2f;
        private const float ShadowReach = 52f;     // DIPs beyond the rug that can be darkened
        private const int ShadowCell = 4;          // the shadow map has one sample per 4x4 pixels

        private readonly int _w, _h, _stride;
        private readonly float _scale;
        private readonly byte[] _face, _back;
        private readonly int _tw, _th;
        private readonly float[] _height;           // height of the rug over each pixel (0 = bare floor), in DIPs
        private volatile float[] _bump;             // light and shade of lumps lying under the rug, per pixel (null = none)

        /// <summary>Lumps under the rug (screen-fixed, so they stay with what's under the rug when the rug is moved).</summary>
        public float[] Bump { set { _bump = value; } }

        private readonly Cloth _cloth;
        private readonly int _dx, _dy;              // size of the smoothed surface (points)
        private readonly float[] _vx, _vy, _vz, _shadeF, _shadeB, _tx, _ty;
        private readonly int[] _quadOrder;
        private readonly float[] _quadKey;
        private readonly int[] _ta, _tb, _tc;
        private readonly bool[] _tBack;
        private readonly int[] _tMinY, _tMaxY;
        private int _triCount;

        // shadows
        private readonly int[] _tapDx, _tapDy;
        private readonly float[] _tapRise;
        private readonly int _sw, _sh;              // shadow map size
        private readonly float[] _shadowA, _shadowB;
        private Int32Rect _heightBox = new Int32Rect(0, 0, 0, 0), _single = new Int32Rect(0, 0, 0, 0);

        /// <summary>Share of the rug's triangles currently showing their underside (for tests).</summary>
        public float BackFacingShare { get; private set; }

        /// <summary>Milliseconds so far in: smoothing, clearing, sorting, drawing, shadows (for tests).</summary>
        public static readonly double[] Profile = new double[5];
        private static long Tick() => System.Diagnostics.Stopwatch.GetTimestamp();
        private static double Ms(long from) => (Tick() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <param name="w">Width of the output in pixels.</param>
        /// <param name="scale">Pixels per DIP.</param>
        /// <param name="texPadPx">Transparent margin around the rug in the textures, in pixels.</param>
        public ClothRenderer(Cloth cloth, int w, int h, float scale, byte[] face, byte[] back, int texW, int texH, float texPadPx)
        {
            _cloth = cloth; _w = w; _h = h; _stride = w * 4; _scale = scale;
            _face = face; _back = back; _tw = texW; _th = texH;
            _height = new float[w * h];

            _dx = 2 * (cloth.Nx - 1) + 1;
            _dy = 2 * (cloth.Ny - 1) + 1;
            int n = _dx * _dy, quads = (_dx - 1) * (_dy - 1);
            _vx = new float[n]; _vy = new float[n]; _vz = new float[n];
            _shadeF = new float[n]; _shadeB = new float[n];
            _tx = new float[n]; _ty = new float[n];
            for (int j = 0; j < _dy; j++)
                for (int i = 0; i < _dx; i++)
                {
                    _tx[j * _dx + i] = texPadPx + (texW - 2 * texPadPx) * i / (_dx - 1);
                    _ty[j * _dx + i] = texPadPx + (texH - 2 * texPadPx) * j / (_dy - 1);
                }
            _quadOrder = new int[quads]; _quadKey = new float[quads];
            _ta = new int[quads * 2]; _tb = new int[quads * 2]; _tc = new int[quads * 2];
            _tBack = new bool[quads * 2]; _tMinY = new int[quads * 2]; _tMaxY = new int[quads * 2];

            // Shadow feelers: look towards the light; anything up there that is tall enough shades this point.
            float[] dist = { 1.5f, 3.5f, 5.5f, 7.5f, 10f, 12.5f, 15.5f, 19f, 23f, 28f, 34f, 41f, 49f };
            float lxy = (float)Math.Sqrt(Lx * Lx + Ly * Ly);
            float tanElevation = Lz / lxy;
            _tapDx = new int[dist.Length]; _tapDy = new int[dist.Length]; _tapRise = new float[dist.Length];
            for (int k = 0; k < dist.Length; k++)
            {
                _tapDx[k] = (int)Math.Round(Lx / lxy * dist[k] * scale);
                _tapDy[k] = (int)Math.Round(Ly / lxy * dist[k] * scale);
                _tapRise[k] = dist[k] * tanElevation;
            }
            _sw = w / ShadowCell + 3; _sh = h / ShadowCell + 3;
            _shadowA = new float[_sw * _sh];
            _shadowB = new float[_sw * _sh];
        }

        /// <summary>Draws the cloth into <paramref name="dst"/> (premultiplied BGRA, w x h). Returns the area that changed.</summary>
        public Int32Rect Render(byte[] dst, DustView dust = null)
        {
            Int32Rect before = _single;
            Int32Rect box = Draw(dst, ref _single, _cloth.X, _cloth.Y, _cloth.Z, dust);
            return Union(before, box);
        }

        /// <summary>
        /// Draws a snapshot of the cloth. <paramref name="previous"/> is where this particular buffer was last drawn into
        /// (so only that has to be wiped); returns the area drawn this time.
        /// </summary>
        public Int32Rect Draw(byte[] dst, ref Int32Rect previous, float[] sx, float[] sy, float[] sz, DustView dust = null)
        {
            long clock = Tick();
            Int32Rect box = Smooth(sx, sy, sz);
            bool dusty = dust != null && dust.Count > 0;
            if (dusty) box = Union(box, Dust.Bounds(dust, _scale, _w, _h));
            Profile[0] += Ms(clock); clock = Tick();

            // wipe what was drawn into this buffer before, and the heights from the last picture
            Int32Rect wipe = Union(previous, box);
            Int32Rect wipeHeights = Union(_heightBox, box);
            Parallel.For(wipe.Y, wipe.Y + wipe.Height, y => Array.Clear(dst, y * _stride + wipe.X * 4, wipe.Width * 4));
            Parallel.For(wipeHeights.Y, wipeHeights.Y + wipeHeights.Height, y => Array.Clear(_height, y * _w + wipeHeights.X, wipeHeights.Width));
            Profile[1] += Ms(clock); clock = Tick();

            BuildTriangles();
            Profile[2] += Ms(clock); clock = Tick();
            int backs = 0;
            for (int t = 0; t < _triCount; t++) if (_tBack[t]) backs++;
            BackFacingShare = _triCount == 0 ? 0 : backs / (float)_triCount;

            const int bandRows = 16;
            int bands = (box.Height + bandRows - 1) / bandRows;
            Parallel.For(0, bands, b =>
            {
                int y0 = box.Y + b * bandRows, y1 = Math.Min(box.Y + box.Height, y0 + bandRows);
                DrawBand(dst, y0, y1);
            });
            Profile[3] += Ms(clock); clock = Tick();

            CastShadows(dst, box);
            Profile[4] += Ms(clock);

            if (dusty)                                              // dust last, and never over the rug: it comes from underneath
            {
                const int dustRows = 32;
                int dustBands = (box.Height + dustRows - 1) / dustRows;
                Parallel.For(0, dustBands, b =>
                {
                    int y0 = box.Y + b * dustRows, y1 = Math.Min(box.Y + box.Height, y0 + dustRows);
                    fixed (byte* d = dst)
                    fixed (float* hb = _height)
                        Dust.Composite(d, _stride, _w, _h, y0, y1, dust, _scale, hb, null, 0);
                });
            }

            previous = box;
            _heightBox = box;
            return box;
        }

        private static Int32Rect Union(Int32Rect a, Int32Rect b)
        {
            if (a.Width <= 0 || a.Height <= 0) return b;
            if (b.Width <= 0 || b.Height <= 0) return a;
            int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
            int x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        }

        // ------------------------------------------------------------------ the smoothed surface

        // Catmull-Rom halfway point between p1 and p2.
        private static float Mid(float p0, float p1, float p2, float p3) => (-p0 + 9 * p1 + 9 * p2 - p3) * (1f / 16f);

        private float At(float[] a, int i, int j)
        {
            int nx = _cloth.Nx, ny = _cloth.Ny;
            if (i < 0) i = 0; else if (i >= nx) i = nx - 1;
            if (j < 0) j = 0; else if (j >= ny) j = ny - 1;
            return a[j * nx + i];
        }

        // Turn the simulated points into a finer, smooth surface, find its bounds, and work out the lighting.
        private Int32Rect Smooth(float[] sx, float[] sy, float[] sz)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            float[][] src = { sx, sy, sz };
            float[][] dstArr = { _vx, _vy, _vz };

            Parallel.For(0, _dy, b =>
            {
                int j = b >> 1;
                bool oddY = (b & 1) == 1;
                for (int a = 0; a < _dx; a++)
                {
                    int i = a >> 1;
                    bool oddX = (a & 1) == 1;
                    int k = b * _dx + a;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float[] s = src[ch];
                        float v;
                        if (!oddX && !oddY) v = At(s, i, j);
                        else if (oddX && !oddY) v = Mid(At(s, i - 1, j), At(s, i, j), At(s, i + 1, j), At(s, i + 2, j));
                        else if (!oddX) v = Mid(At(s, i, j - 1), At(s, i, j), At(s, i, j + 1), At(s, i, j + 2));
                        else
                        {
                            float r0 = Mid(At(s, i - 1, j - 1), At(s, i, j - 1), At(s, i + 1, j - 1), At(s, i + 2, j - 1));
                            float r1 = Mid(At(s, i - 1, j), At(s, i, j), At(s, i + 1, j), At(s, i + 2, j));
                            float r2 = Mid(At(s, i - 1, j + 1), At(s, i, j + 1), At(s, i + 1, j + 1), At(s, i + 2, j + 1));
                            float r3 = Mid(At(s, i - 1, j + 2), At(s, i, j + 2), At(s, i + 1, j + 2), At(s, i + 2, j + 2));
                            v = Mid(r0, r1, r2, r3);
                        }
                        dstArr[ch][k] = v;
                    }
                }
            });

            for (int k = 0; k < _vx.Length; k++)
            {
                float px = _vx[k] * _scale, py = _vy[k] * _scale;
                _vx[k] = px; _vy[k] = py;                       // from now on in pixels
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
            }

            // lighting from the surface's own slope (central differences on the fine grid; z is in DIPs, so scale x/y back)
            float inv = 1f / _scale;
            Parallel.For(0, _dy, b =>
            {
                int bp = Math.Min(b + 1, _dy - 1), bm = Math.Max(b - 1, 0);
                for (int a = 0; a < _dx; a++)
                {
                    int ap = Math.Min(a + 1, _dx - 1), am = Math.Max(a - 1, 0);
                    int e = b * _dx + ap, w = b * _dx + am, s = bp * _dx + a, n = bm * _dx + a;
                    float ax = (_vx[e] - _vx[w]) * inv, ay = (_vy[e] - _vy[w]) * inv, az = _vz[e] - _vz[w];
                    float bx = (_vx[s] - _vx[n]) * inv, by = (_vy[s] - _vy[n]) * inv, bz = _vz[s] - _vz[n];
                    float nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    int k = b * _dx + a;
                    if (len < 1e-6f) { _shadeF[k] = Ambient + Diffuse * Lz; _shadeB[k] = Ambient; continue; }
                    float dot = (nx * Lx + ny * Ly + nz * Lz) / len;
                    _shadeF[k] = Ambient + Diffuse * Math.Max(0, dot);
                    _shadeB[k] = Ambient + Diffuse * Math.Max(0, -dot);
                }
            });

            int margin = (int)(ShadowReach * _scale);
            int x0 = Math.Max(0, (int)minX - margin), y0 = Math.Max(0, (int)minY - margin);
            int x1 = Math.Min(_w, (int)maxX + margin + 1), y1 = Math.Min(_h, (int)maxY + margin + 1);
            return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        // Sort cells from the lowest to the highest and turn them into triangles (painter's algorithm).
        private void BuildTriangles()
        {
            int qx = _dx - 1, quads = qx * (_dy - 1);
            for (int q = 0; q < quads; q++)
            {
                int i = q % qx, j = q / qx, k = j * _dx + i;
                _quadOrder[q] = q;
                _quadKey[q] = (_vz[k] + _vz[k + 1] + _vz[k + _dx] + _vz[k + _dx + 1]) * 0.25f;
            }
            Array.Sort(_quadKey, _quadOrder, 0, quads);

            _triCount = 0;
            for (int n = 0; n < quads; n++)
            {
                int q = _quadOrder[n], i = q % qx, j = q / qx, k = j * _dx + i;
                AddTriangle(k, k + 1, k + _dx);
                AddTriangle(k + 1, k + _dx + 1, k + _dx);
            }
        }

        private void AddTriangle(int a, int b, int c)
        {
            float area = (_vx[b] - _vx[a]) * (_vy[c] - _vy[a]) - (_vy[b] - _vy[a]) * (_vx[c] - _vx[a]);
            if (Math.Abs(area) < 1e-4f) return;
            bool back = area < 0;
            if (back) { int t = b; b = c; c = t; }              // always walk the corners the same way round

            int t0 = _triCount++;
            _ta[t0] = a; _tb[t0] = b; _tc[t0] = c; _tBack[t0] = back;
            _tMinY[t0] = (int)Math.Floor(Math.Min(_vy[a], Math.Min(_vy[b], _vy[c])));
            _tMaxY[t0] = (int)Math.Ceiling(Math.Max(_vy[a], Math.Max(_vy[b], _vy[c])));
        }

        // ------------------------------------------------------------------ drawing

        private void DrawBand(byte[] dst, int y0, int y1)
        {
            fixed (byte* d = dst)
            fixed (float* hb = _height)
            fixed (byte* faceTex = _face)
            fixed (byte* backTex = _back)
            fixed (float* bump = _bump)
            {
                for (int t = 0; t < _triCount; t++)
                {
                    if (_tMaxY[t] < y0 || _tMinY[t] >= y1) continue;
                    DrawTriangle(d, hb, _tBack[t] ? backTex : faceTex, t, y0, y1, bump);
                }
            }
        }

        private void DrawTriangle(byte* dst, float* height, byte* tex, int t, int rowFrom, int rowTo, float* bump)
        {
            int ia = _ta[t], ib = _tb[t], ic = _tc[t];
            float ax = _vx[ia], ay = _vy[ia], bx = _vx[ib], by = _vy[ib], cx = _vx[ic], cy = _vy[ic];
            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (area <= 0) return;
            float inv = 1f / area;
            bool back = _tBack[t];
            float[] shade = back ? _shadeB : _shadeF;
            float sa = shade[ia], sb = shade[ib], sc = shade[ic];
            float za = _vz[ia], zb = _vz[ib], zc = _vz[ic];
            float ua = _tx[ia], ub = _tx[ib], uc = _tx[ic], va = _ty[ia], vb = _ty[ib], vc = _ty[ic];

            int xMin = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int xMax = Math.Min(_w - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int yMin = Math.Max(rowFrom, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
            int yMax = Math.Min(rowTo - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));

            // Edge ownership so that two triangles sharing an edge never both (or neither) draw a pixel on it.
            bool ownA = (cy - by) > 0 || ((cy - by) == 0 && (cx - bx) < 0);   // edge b->c
            bool ownB = (ay - cy) > 0 || ((ay - cy) == 0 && (ax - cx) < 0);   // edge c->a
            bool ownC = (by - ay) > 0 || ((by - ay) == 0 && (bx - ax) < 0);   // edge a->b

            // how each edge function changes one pixel along the row
            float dAx = -(cy - by), dBx = -(ay - cy), dCx = -(by - ay);
            int texLast = _tw - 1, texLastY = _th - 1;

            for (int y = yMin; y <= yMax; y++)
            {
                float py = y + 0.5f, px0 = xMin + 0.5f;
                float wa = (cx - bx) * (py - by) - (cy - by) * (px0 - bx);   // weight of a = edge(b, c, p), stepped along the row
                float wb = (ax - cx) * (py - cy) - (ay - cy) * (px0 - cx);
                float wc = (bx - ax) * (py - ay) - (by - ay) * (px0 - ax);
                for (int x = xMin; x <= xMax; x++, wa += dAx, wb += dBx, wc += dCx)
                {
                    if (wa < 0 || (wa == 0 && !ownA)) continue;
                    if (wb < 0 || (wb == 0 && !ownB)) continue;
                    if (wc < 0 || (wc == 0 && !ownC)) continue;
                    float na = wa * inv, nb = wb * inv, nc = wc * inv;

                    float z = na * za + nb * zb + nc * zc;
                    float* hp = height + y * _w + x;
                    if (z < *hp - 0.5f) continue;                  // hidden behind a higher bit of rug

                    float fx = na * ua + nb * ub + nc * uc - 0.5f, fy = na * va + nb * vb + nc * vc - 0.5f;
                    int x0 = fx >= 0 ? (int)fx : (int)fx - 1, y0 = fy >= 0 ? (int)fy : (int)fy - 1;
                    float ex = fx - x0, ey = fy - y0;
                    int x1 = x0 + 1, y1 = y0 + 1;
                    if (x0 < 0) x0 = 0; else if (x0 > texLast) x0 = texLast;
                    if (x1 < 0) x1 = 0; else if (x1 > texLast) x1 = texLast;
                    if (y0 < 0) y0 = 0; else if (y0 > texLastY) y0 = texLastY;
                    if (y1 < 0) y1 = 0; else if (y1 > texLastY) y1 = texLastY;
                    byte* p00 = tex + (y0 * _tw + x0) * 4, p10 = tex + (y0 * _tw + x1) * 4;
                    byte* p01 = tex + (y1 * _tw + x0) * 4, p11 = tex + (y1 * _tw + x1) * 4;
                    float w00 = (1 - ex) * (1 - ey), w10 = ex * (1 - ey), w01 = (1 - ex) * ey, w11 = ex * ey;
                    float a = p00[3] * w00 + p10[3] * w10 + p01[3] * w01 + p11[3] * w11;
                    if (a < 1f) continue;

                    float lit = na * sa + nb * sb + nc * sc;
                    if (bump != null)                              // lying over lumps: they show through, less so the higher the rug is lifted
                    {
                        float bs = bump[y * _w + x];
                        if (bs != 0f) lit *= 1f + bs * (z < Cloth.Floor + 8f ? 1f : Math.Max(0f, 1f - (z - Cloth.Floor - 8f) * 0.1f));
                    }
                    float b = (p00[0] * w00 + p10[0] * w10 + p01[0] * w01 + p11[0] * w11) * lit;
                    float g = (p00[1] * w00 + p10[1] * w10 + p01[1] * w01 + p11[1] * w11) * lit;
                    float r = (p00[2] * w00 + p10[2] * w10 + p01[2] * w01 + p11[2] * w11) * lit;
                    if (b > a) b = a; if (g > a) g = a; if (r > a) r = a;

                    byte* o = dst + y * _stride + x * 4;
                    float keep = 1f - a * (1f / 255f);
                    o[0] = (byte)(b + o[0] * keep);
                    o[1] = (byte)(g + o[1] * keep);
                    o[2] = (byte)(r + o[2] * keep);
                    o[3] = (byte)(a + o[3] * keep);

                    if (a > 128f && z > *hp) *hp = z;
                }
            }
        }

        // ------------------------------------------------------------------ shadows

        // Look towards the light from every point of a coarse map: if the rug stands high enough over there, the point
        // is in its shadow. Then smooth that map and lay it over the picture, so shadow edges are soft.
        private void CastShadows(byte[] dst, Int32Rect box)
        {
            int hx0 = box.X / ShadowCell, hy0 = box.Y / ShadowCell;
            int hx1 = Math.Min(_sw - 2, (box.X + box.Width) / ShadowCell + 1), hy1 = Math.Min(_sh - 2, (box.Y + box.Height) / ShadowCell + 1);

            Parallel.For(hy0, hy1 + 1, hy =>
            {
                fixed (float* hb = _height)
                fixed (float* sm = _shadowA)
                {
                    int y = Math.Min(_h - 1, hy * ShadowCell);
                    for (int hx = hx0; hx <= hx1; hx++)
                    {
                        int x = Math.Min(_w - 1, hx * ShadowCell);
                        float here = hb[y * _w + x];
                        float s = 0;
                        for (int k = 0; k < _tapDx.Length; k++)
                        {
                            int qx = x + _tapDx[k], qy = y + _tapDy[k];
                            if (qx < 0 || qy < 0 || qx >= _w || qy >= _h) continue;
                            float t = (hb[qy * _w + qx] - here - _tapRise[k]) * (1f / ShadowSoftness);
                            if (t > s) { s = t; if (s >= 1f) { s = 1f; break; } }
                        }
                        sm[hy * _sw + hx] = s;
                    }
                }
            });

            Parallel.For(hy0, hy1 + 1, hy =>
            {
                for (int hx = hx0; hx <= hx1; hx++)
                {
                    float sum = 0;
                    int n = 0;
                    for (int oy = -1; oy <= 1; oy++)
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int yy = hy + oy, xx = hx + ox;
                            if (yy < hy0 || xx < hx0 || yy > hy1 || xx > hx1) continue;
                            sum += _shadowA[yy * _sw + xx];
                            n++;
                        }
                    _shadowB[hy * _sw + hx] = sum / n;
                }
            });

            // which rows of the coarse map have any shadow at all
            var rowHasShadow = new bool[_sh];
            for (int hy = hy0; hy <= hy1; hy++)
                for (int hx = hx0; hx <= hx1; hx++)
                    if (_shadowB[hy * _sw + hx] > 0.004f) { rowHasShadow[hy] = true; break; }

            const float inv = 1f / ShadowCell;
            Parallel.For(box.Y, box.Y + box.Height, y =>
            {
                fixed (byte* d = dst)
                fixed (float* hb = _height)
                {
                    float fy = y * inv;
                    int sy = Math.Min(hy1 - 1, (int)fy);
                    if (!rowHasShadow[sy] && !rowHasShadow[sy + 1]) return;
                    float ty = fy - sy;
                    for (int x = box.X; x < box.X + box.Width; x++)
                    {
                        float fx = x * inv;
                        int sx = Math.Min(hx1 - 1, (int)fx);
                        float tx = fx - sx;
                        float a = _shadowB[sy * _sw + sx], b = _shadowB[sy * _sw + sx + 1];
                        float c = _shadowB[(sy + 1) * _sw + sx], e = _shadowB[(sy + 1) * _sw + sx + 1];
                        float s = (a + (b - a) * tx) * (1 - ty) + (c + (e - c) * tx) * ty;
                        if (s <= 0.004f) continue;

                        byte* o = d + y * _stride + x * 4;
                        if (hb[y * _w + x] > 0)                         // on the rug: it just gets darker
                        {
                            float dark = 1f - ClothShadow * s;
                            o[0] = (byte)(o[0] * dark); o[1] = (byte)(o[1] * dark); o[2] = (byte)(o[2] * dark);
                        }
                        else                                            // bare floor: lay a translucent shadow down
                        {
                            float add = FloorShadow * s * 255f * (1f - o[3] * (1f / 255f));
                            o[3] = (byte)Math.Min(255f, o[3] + add);
                        }
                    }
                }
            });
        }
    }
}
