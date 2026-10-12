using System;
using System.Runtime.InteropServices;

namespace AladdinRug
{
    /// <summary>
    /// The rug as a piece of heavy cloth: a grid of points joined by springs that resist stretching, a little
    /// shearing and a lot of sharp bending. It lies on a floor with friction, layers stack when it folds over
    /// itself, and the mouse is a hand that can pinch any patch of it and lift or drag it.
    /// Position-based dynamics: move everything, then repeatedly nudge the points to satisfy the springs.
    /// Units are DIPs; x/y are screen axes (y down) and z is height above the floor.
    ///
    /// Not thread-safe by itself: <see cref="ClothEngine"/> runs it on one thread and guards the calls that change it.
    /// </summary>
    internal sealed unsafe class Cloth
    {
        public const float Floor = 5f;          // height of the lowest layer of rug above the floor
        public const float Layer = 6f;          // how much room each layer of rug takes when stacked
        public const float HandLift = 26f;      // how high the hand lifts the rug to fold it over itself
        public const float LiftLow = 8f;        // ...and how low it stays when it is just dragging the rug along the floor
        public const float LiftMid = 15f;       // ...and when it has hold of the middle, where there's no obvious direction

        private const int Substeps = 3, Iterations = 6;
        private const float Damping = 0.997f, MaxHandStep = 7f, MaxPointStep = 11f;
        private const float StretchEase = 1.06f, StretchStop = 1.28f;    // the hand slows once the weave is stretched this much, and stops at the second
        private const float StretchCap = 1.025f;                          // threads are held to this much stretch
        private const float ImpactSpeed = 0.45f;
        private const float HandGather = 0.28f;                // how much a lifted handful draws in, per unit of lift over the handful's radius
        private const float PatchGaps = 2.6f;               // the size of a handful, in gaps between points     // how fast (per substep) an edge must hit the floor to raise dust

        private readonly ClothFeel _feel;

        [StructLayout(LayoutKind.Sequential)]
        private struct Spring { public int A, B; public float Rest, Stretch, Squash; }   // stiffness when stretched / squashed

        public readonly int Nx, Ny, Count;
        public readonly float Sx, Sy;                   // gap between neighbouring points at rest
        public readonly float[] X, Y, Z;

        private readonly float[] _px, _py, _pz, _inv;   // previous positions; inverse mass (0 = held by the hand)
        private readonly float[] _offX, _offY;          // flat layout, relative to the rug's centre
        private readonly Spring[] _springs;
        private readonly Spring[] _threads;             // just the woven threads (the neighbours along each row and column), for limiting stretch
        private readonly float _w, _h;                  // the room the rug can move in

        private readonly ClothCollision _collision;

        // hand
        private int[] _held = new int[0];
        private float[] _heldOffX = new float[0], _heldOffY = new float[0], _heldZ0 = new float[0], _heldFall = new float[0];
        private volatile float _tx, _ty;                   // where the mouse is (written by the UI thread)
        private float _hx, _hy, _lift, _patch, _liftGoal = LiftMid;

        public float Speed { get; private set; }       // how much the rug moved in the last step (DIPs)
        public float MaxStretch { get; private set; } = 1f;   // the most any thread is stretched (1 = not at all)

        // Where the rug's edge slapped the floor during the last step (for dust): position, direction away from the rug, and force 0..1.
        public readonly float[] ImpX = new float[384], ImpY = new float[384], ImpDX = new float[384], ImpDY = new float[384], ImpP = new float[384];
        public int ImpactCount;
        private readonly bool[] _edge;
        private readonly float[] _vdown;                // how fast each point was heading for the floor at the start of the last substep
        private float _cx, _cy;
        public bool IsHeld => _held.Length > 0;
        /// <summary>How far the hand is behind the mouse (DIPs): the rug's weight holding it back.</summary>
        public float HandLag => IsHeld ? MathF.Sqrt((_tx - _hx) * (_tx - _hx) + (_ty - _hy) * (_ty - _hy)) : 0f;

        public Cloth(float left, float top, float width, float height, float roomW, float roomH, float targetGap, ClothFeel feel = null)
        {
            _feel = feel ?? new ClothFeel();
            Nx = Math.Max(4, (int)Math.Round(width / targetGap) + 1);
            Ny = Math.Max(4, (int)Math.Round(height / targetGap) + 1);
            Count = Nx * Ny;
            Sx = width / (Nx - 1);
            Sy = height / (Ny - 1);
            _w = roomW;
            _h = roomH;

            X = new float[Count]; Y = new float[Count]; Z = new float[Count];
            _px = new float[Count]; _py = new float[Count]; _pz = new float[Count];
            _inv = new float[Count];
            _offX = new float[Count]; _offY = new float[Count];
            _edge = new bool[Count];
            _vdown = new float[Count];

            var rnd = new Random(5);
            for (int j = 0; j < Ny; j++)
                for (int i = 0; i < Nx; i++)
                {
                    int k = j * Nx + i;
                    X[k] = left + i * Sx;
                    Y[k] = top + j * Sy;
                    Z[k] = Floor + (float)rnd.NextDouble() * 0.04f;     // a whisper of noise so a squeezed rug can buckle
                    _px[k] = X[k]; _py[k] = Y[k]; _pz[k] = Z[k];
                    _inv[k] = 1;
                    _offX[k] = (i - (Nx - 1) / 2f) * Sx;
                    _offY[k] = (j - (Ny - 1) / 2f) * Sy;
                    _edge[k] = i <= 1 || j <= 1 || i >= Nx - 2 || j >= Ny - 2;
                }

            var springs = new System.Collections.Generic.List<Spring>();
            var threads = new System.Collections.Generic.List<Spring>();
            void Add(int a, int b, float rest, float stretch, float squash) => springs.Add(new Spring { A = a, B = b, Rest = rest, Stretch = stretch, Squash = squash });
            void Thread(int a, int b, float rest, float squash) { Add(a, b, rest, 1.0f, squash); threads.Add(springs[springs.Count - 1]); }
            float diag = (float)Math.Sqrt(Sx * Sx + Sy * Sy);
            float squash = _feel.Squash, diagSquash = Math.Min(1f, 0.95f * _feel.Squash / 0.70f), bend = _feel.BendSquash, bendPull = _feel.BendStretch;
            for (int j = 0; j < Ny; j++)
                for (int i = 0; i < Nx; i++)
                {
                    int k = j * Nx + i;
                    if (i + 1 < Nx) Thread(k, k + 1, Sx, squash);                  // woven threads: won't stretch
                    if (j + 1 < Ny) Thread(k, k + Nx, Sy, squash);
                    if (i + 1 < Nx && j + 1 < Ny) { Add(k, k + Nx + 1, diag, 1.0f, diagSquash); Add(k + 1, k + Nx, diag, 1.0f, diagSquash); }
                    if (i + 2 < Nx) Add(k, k + 2, 2 * Sx, bendPull, bend);             // thick wool resists sharp bends
                    if (j + 2 < Ny) Add(k, k + 2 * Nx, 2 * Sy, bendPull, bend);
                }
            // The weave doesn't shear: the diagonals get a second say at the end of every pass, so a cell stays square even
            // when the hand drags the rug hard.
            int cells = springs.Count;
            for (int n = 0; n < cells; n++)
                if (springs[n].Rest == diag) springs.Add(springs[n]);
            _springs = springs.ToArray();
            _threads = threads.ToArray();

            _collision = new ClothCollision(Nx, Ny);
        }

        public float CentreX { get { float s = 0; for (int i = 0; i < Count; i++) s += X[i]; return s / Count; } }
        public float CentreY { get { float s = 0; for (int i = 0; i < Count; i++) s += Y[i]; return s / Count; } }

        /// <summary>Copy the current positions (for drawing on another thread).</summary>
        public void CopyPositions(float[] x, float[] y, float[] z)
        {
            Array.Copy(X, x, Count);
            Array.Copy(Y, y, Count);
            Array.Copy(Z, z, Count);
        }

        // ------------------------------------------------------------------ the hand

        /// <summary>Pinch the top-most bit of rug under (mx, my). Returns false if there is none.</summary>
        public bool Grab(float mx, float my)
        {
            float reach = Math.Max(Sx, Sy) * 1.05f;
            int best = -1;
            float bestZ = float.MinValue;
            for (int i = 0; i < Count; i++)
            {
                float dx = X[i] - mx, dy = Y[i] - my;
                if (dx * dx + dy * dy < reach * reach && Z[i] > bestZ) { bestZ = Z[i]; best = i; }
            }
            if (best < 0) return false;

            _patch = Math.Max(Sx, Sy) * PatchGaps;
            float patch = _patch;
            var idx = new System.Collections.Generic.List<int>();
            for (int i = 0; i < Count; i++)
            {
                float dx = X[i] - mx, dy = Y[i] - my;
                if (dx * dx + dy * dy < patch * patch && Z[i] > bestZ - Layer * 0.9f) idx.Add(i);
            }
            var held = idx.ToArray();
            var offX = new float[held.Length]; var offY = new float[held.Length]; var z0 = new float[held.Length]; var fall = new float[held.Length];
            for (int n = 0; n < held.Length; n++)
            {
                int i = held[n];
                offX[n] = X[i] - mx;
                offY[n] = Y[i] - my;
                z0[n] = Z[i] - Floor;
                float d = (float)Math.Sqrt(offX[n] * offX[n] + offY[n] * offY[n]) / _patch;
                float c = (float)Math.Cos(Math.Min(1f, d) * Math.PI / 2);
                fall[n] = 0.08f + 0.92f * c * c;                    // a smooth hump, so the cloth doesn't step up a cliff at the edge of the handful
                _inv[i] = 0;
            }
            _heldOffX = offX; _heldOffY = offY; _heldZ0 = z0; _heldFall = fall;
            _held = held;
            _hx = _tx = mx;
            _hy = _ty = my;
            _lift = 0;
            _liftGoal = LiftMid;
            return true;
        }

        public void MoveHand(float mx, float my)
        {
            _tx = mx;
            _ty = my;
        }

        public void Release()
        {
            foreach (int i in _held) _inv[i] = 1;
            _held = new int[0];
        }

        // ------------------------------------------------------------------ simulation

        /// <summary>Milliseconds spent so far in: moving, reserved, springs, surface contact/settling, reserved (for tests).</summary>
        public static readonly double[] Profile = new double[5];
        private static long Tick() => System.Diagnostics.Stopwatch.GetTimestamp();
        private static double Ms(long from) => (Tick() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        public void Step(float dt)
        {
            ImpactCount = 0;
            _cx = CentreX; _cy = CentreY;
            float h = dt / Substeps, moved = 0;
            for (int s = 0; s < Substeps; s++)
            {
                _collision.Begin(X, Y, Z);
                float handX = _hx, handY = _hy, lift = _lift;
                long t = Tick();
                MoveHandAndPoints(h);
                Profile[0] += Ms(t); t = Tick();
                for (int it = 0; it < Iterations; it++) SolveSprings();
                LimitStrain();
                Profile[2] += Ms(t); t = Tick();
                for (int contact = 0; contact < 2; contact++)
                {
                    _collision.Project(X, Y, Z, _inv, Layer);
                    // Surface contact can stretch the weave; keep the two constraints coupled.
                    LimitStrain();
                    Settle(contact == 0);     // repeat the clamps; friction and dust still run once per substep
                }
                // All springs, contact projections and floor/wall clamps are included in this swept move.
                // There is no later positional correction that can undo the acceptance check.
                float accepted = _collision.LimitMotion(X, Y, Z);
                if (accepted < 1)
                {
                    _hx = handX + (_hx - handX) * accepted;
                    _hy = handY + (_hy - handY) * accepted;
                    _lift = lift + (_lift - lift) * accepted;
                    Freeze();
                }
                moved += _collision.AcceptedDistance(X, Y, Z, _inv);
                Profile[3] += Ms(t);
            }
            Measure(moved);
        }

        // Pulling the rug along the floor, you hold it low and it slides; pushing a part of it back over the rest, you
        // lift it and it folds. So: moving away from the middle of the rug = low, moving towards it = high.
        private void DecideLift(float mx, float my)
        {
            float speed = (float)Math.Sqrt(mx * mx + my * my);
            if (speed < 0.25f) return;                                   // hardly moving: keep what we decided

            float cx = CentreX, cy = CentreY;
            float toX = cx - _hx, toY = cy - _hy, dist = (float)Math.Sqrt(toX * toX + toY * toY);
            float size = (float)Math.Sqrt((Nx - 1) * Sx * (Nx - 1) * Sx + (Ny - 1) * Sy * (Ny - 1) * Sy);
            float far = Clamp((dist / size - 0.08f) / 0.17f, 0f, 1f);   // 0 at the very middle, 1 out near the edges
            float along = (mx * toX + my * toY) / (speed * Math.Max(dist, 1e-3f));   // +1 = heading for the middle

            float t = Clamp((along + 0.2f) / 0.8f, 0f, 1f);
            t = t * t * (3 - 2 * t);
            float edgeGoal = LiftLow + (HandLift - LiftLow) * t;
            float goal = LiftMid + (edgeGoal - LiftMid) * far;
            _liftGoal += (goal - _liftGoal) * 0.08f;                      // change your mind gradually
        }

        private void MoveHandAndPoints(float h)
        {
            // The hand glides to where the mouse is (never leaps, so the rug can't be torn apart), and lifts.
            int[] held = _held;
            if (held.Length > 0)
            {
                float mx = Clamp(_tx - _hx, -MaxHandStep, MaxHandStep), my = Clamp(_ty - _hy, -MaxHandStep, MaxHandStep);

                // A rug is woven: it doesn't stretch. If the hand has pulled the cloth as far as it will go, the hand has to
                // wait for the rest of the rug to be dragged along (the weight of the wool) instead of tearing away from it.
                float hold = Clamp(1f - (MaxStretch - StretchEase) / (StretchStop - StretchEase), 0.04f, 1f);
                mx *= hold;
                my *= hold;
                _hx += mx;
                _hy += my;
                DecideLift(mx, my);
                _lift += (_liftGoal - _lift) * 0.12f;
            }

            float g = _feel.Gravity * h * h;
            float air = _feel.Air;
            float airborne = Floor + 2 * Layer + 1;
            fixed (float* x = X, y = Y, z = Z, px = _px, py = _py, pz = _pz, inv = _inv, vd = _vdown)
            {
                for (int i = 0; i < Count; i++)
                {
                    if (inv[i] == 0) continue;
                    float damp = z[i] > airborne ? air : Damping;     // a lifted rug hangs; it doesn't fly
                    float vx = (x[i] - px[i]) * damp, vy = (y[i] - py[i]) * damp, vz = (z[i] - pz[i]) * damp;
                    float speed2 = vx * vx + vy * vy + vz * vz;
                    if (speed2 > MaxPointStep * MaxPointStep) { float k = MaxPointStep / MathF.Sqrt(speed2); vx *= k; vy *= k; vz *= k; }
                    px[i] = x[i]; py[i] = y[i]; pz[i] = z[i];
                    vd[i] = g - vz;
                    x[i] += vx; y[i] += vy; z[i] += vz - g;
                }
                // Lifting a handful draws the cloth in towards the hand (it can't stretch to reach), like gathering a sheet.
                float squeeze = Clamp(1f - HandGather * _lift / Math.Max(_patch, 1f), 0.6f, 1f);
                for (int n = 0; n < held.Length; n++)
                {
                    int i = held[n];
                    px[i] = x[i]; py[i] = y[i]; pz[i] = z[i];
                    x[i] = _hx + _heldOffX[n] * squeeze;
                    y[i] = _hy + _heldOffY[n] * squeeze;
                    z[i] = Floor + _heldZ0[n] + _lift * _heldFall[n];
                }
            }
        }

        private void SolveSprings()
        {
            fixed (Spring* springs = _springs)
            fixed (float* x = X, y = Y, z = Z, inv = _inv)
            {
                Spring* end = springs + _springs.Length;
                for (Spring* s = springs; s < end; s++)
                {
                    int a = s->A, b = s->B;
                    float wa = inv[a], wb = inv[b], w = wa + wb;
                    if (w == 0f) continue;

                    float dx = x[b] - x[a], dy = y[b] - y[a], dz = z[b] - z[a];
                    float len2 = dx * dx + dy * dy + dz * dz;
                    if (len2 < 1e-10f) continue;

                    float r = MathF.ReciprocalSqrtEstimate(len2);        // 1 / length, to ~12 bits, then one refinement
                    r *= 1.5f - 0.5f * len2 * r * r;
                    float len = len2 * r;
                    float k = len > s->Rest ? s->Stretch : s->Squash;
                    float f = (1f - s->Rest * r) * k * (w > 1.5f ? 0.5f : 1f);     // w is 1 (one end held) or 2
                    float fa = wa * f, fb = wb * f;
                    x[a] += fa * dx; y[a] += fa * dy; z[a] += fa * dz;
                    x[b] -= fb * dx; y[b] -= fb * dy; z[b] -= fb * dz;
                }
            }
        }

        // The weave can't be stretched: whatever the springs left over, hold every thread to a hair over its length.
        // Done in both directions along the threads so the correction travels from the hand out to the far end.
        private void LimitStrain()
        {
            fixed (Spring* th = _threads)
            fixed (float* x = X, y = Y, z = Z, inv = _inv)
            {
                int count = _threads.Length;
                for (int pass = 0; pass < 4; pass++)
                    for (int n = 0; n < count; n++)
                    {
                        Spring* s = th + ((pass & 1) == 0 ? n : count - 1 - n);
                        int a = s->A, b = s->B;
                        float wa = inv[a], wb = inv[b];
                        float w = wa + wb;
                        if (w == 0f) continue;
                        float dx = x[b] - x[a], dy = y[b] - y[a];
                        float len = MathF.Sqrt(dx * dx + dy * dy), max = s->Rest * StretchCap;     // sideways only: the layers' own gap isn't stretch
                        if (len <= max) continue;
                        float k = (len - max) / (len * w);
                        x[a] += wa * k * dx; y[a] += wa * k * dy;
                        x[b] -= wb * k * dx; y[b] -= wb * k * dy;
                    }
            }
        }

        // The floor holds the rug up and grips it; the walls of the room keep it on screen.
        private void Settle(bool applyFriction)
        {
            float grip = Floor + 2 * Layer + 1;
            float friction = _feel.Friction;
            fixed (float* x = X, y = Y, z = Z, px = _px, py = _py, pz = _pz, inv = _inv)
            {
                for (int i = 0; i < Count; i++)
                {
                    if (z[i] < Floor)
                    {
                        float fall = _vdown[i];
                        if (applyFriction && fall > ImpactSpeed && _edge[i] && ImpactCount < ImpX.Length) NoteImpact(x[i], y[i], fall);
                        z[i] = Floor; pz[i] = Floor;
                    }
                    if (inv[i] == 0) continue;

                    if (x[i] < 2) x[i] = 2; else if (x[i] > _w - 2) x[i] = _w - 2;
                    if (y[i] < 2) y[i] = 2; else if (y[i] > _h - 2) y[i] = _h - 2;

                    if (applyFriction && z[i] < grip)    // lying on the floor or on other rug: heavy wool doesn't slide easily
                    {
                        px[i] = x[i] - (x[i] - px[i]) * friction;
                        py[i] = y[i] - (y[i] - py[i]) * friction;
                    }
                }
            }
        }

        private void NoteImpact(float x, float y, float fall)
        {
            int k = ImpactCount++;
            float dx = x - _cx, dy = y - _cy, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-3f) { dx = 1; dy = 0; len = 1; }
            ImpX[k] = x; ImpY[k] = y; ImpDX[k] = dx / len; ImpDY[k] = dy / len;
            ImpP[k] = Math.Min(1f, (fall - ImpactSpeed) / 0.9f);
        }

        private void Measure(float acceptedDistance)
        {
            // Contact damping resets Verlet velocity; sleep must use the movement actually accepted.
            Speed = acceptedDistance;

            float stretch = 1f;                            // the most stretched thread
            foreach (Spring sp in _threads)
            {
                float dx = X[sp.B] - X[sp.A], dy = Y[sp.B] - Y[sp.A];       // sideways length: layers stacked on layers aren't a pull
                float r = MathF.Sqrt(dx * dx + dy * dy) / sp.Rest;
                if (r > stretch) stretch = r;
            }
            MaxStretch = stretch;
            if (float.IsNaN(Speed) || float.IsInfinity(Speed)) Recover();
        }

        // Something numerical went badly wrong: put the rug back flat in the middle of the room rather than keep going.
        private void Recover()
        {
            Release();
            for (int i = 0; i < Count; i++)
            {
                X[i] = _w / 2 + _offX[i]; Y[i] = _h / 2 + _offY[i]; Z[i] = Floor;
                _px[i] = X[i]; _py[i] = Y[i]; _pz[i] = Z[i];
            }
            Speed = 0;
        }

        /// <summary>
        /// How far the woven grid has been pushed out of square (degrees away from a right angle between a cell's two edges):
        /// the 95th percentile over all cells, and the worst one. A real rug barely shears at all.
        /// </summary>

        /// <summary>How much the woven threads have been stretched: edge length over rest length, 95th percentile and worst (1 = none).</summary>
        public void StretchStats(out float p95, out float worst)
        {
            var ratio = new float[2 * Count];
            int n = 0;
            worst = 1;
            for (int j = 0; j < Ny; j++)
                for (int i = 0; i < Nx; i++)
                {
                    int k = j * Nx + i;
                    if (i + 1 < Nx) { float r = Dist(k, k + 1) / Sx; ratio[n++] = r; if (r > worst) worst = r; }
                    if (j + 1 < Ny) { float r = Dist(k, k + Nx) / Sy; ratio[n++] = r; if (r > worst) worst = r; }
                }
            Array.Sort(ratio, 0, n);
            p95 = n == 0 ? 1 : ratio[(int)(n * 0.95)];
        }

        private float Dist(int a, int b)
        {
            float dx = X[a] - X[b], dy = Y[a] - Y[b];
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        public void ShearStats(out float p95, out float worst)
        {
            var dev = new float[(Nx - 1) * (Ny - 1)];
            int n = 0;
            worst = 0;
            for (int j = 0; j < Ny - 1; j++)
                for (int i = 0; i < Nx - 1; i++)
                {
                    int k = j * Nx + i;
                    float ax = X[k + 1] - X[k], ay = Y[k + 1] - Y[k], az = Z[k + 1] - Z[k];
                    float bx = X[k + Nx] - X[k], by = Y[k + Nx] - Y[k], bz = Z[k + Nx] - Z[k];
                    // only cells lying flat (the rest are bent on purpose): is the corner still a right angle as seen from above?
                    float zmin = Math.Min(Math.Min(Z[k], Z[k + 1]), Math.Min(Z[k + Nx], Z[k + Nx + 1])), zmax = Math.Max(Math.Max(Z[k], Z[k + 1]), Math.Max(Z[k + Nx], Z[k + Nx + 1]));
                    if (zmax - zmin > 4f) continue;
                    az = 0; bz = 0;
                    float la = MathF.Sqrt(ax * ax + ay * ay + az * az), lb = MathF.Sqrt(bx * bx + by * by + bz * bz);
                    if (la < 1e-3f || lb < 1e-3f) continue;
                    float c = Math.Max(-1f, Math.Min(1f, (ax * bx + ay * by + az * bz) / (la * lb)));
                    float d = MathF.Abs(MathF.Acos(c) * 57.29578f - 90f);
                    dev[n++] = d;
                    if (d > worst) worst = d;
                }
            Array.Sort(dev, 0, n);
            p95 = n == 0 ? 0 : dev[(int)(n * 0.95)];
        }

        /// <summary>How many nonadjacent triangle surfaces intersect (should be zero).</summary>
        public int Penetrations() => _collision.Intersections(X, Y, Z);

        /// <summary>Stop all motion where it lies (the rug has come to rest).</summary>
        public void Freeze()
        {
            for (int i = 0; i < Count; i++) { _px[i] = X[i]; _py[i] = Y[i]; _pz[i] = Z[i]; }
            Speed = 0;
        }

        // ------------------------------------------------------------------ laying it flat again

        private float[] _fromX, _fromY, _fromZ, _toX, _toY;

        /// <summary>Plan to ease every point back to the flat layout, centred where the rug is now.</summary>
        public void BeginFlatten(float roomLeft, float roomTop, float roomRight, float roomBottom)
        {
            Release();
            float cx = CentreX, cy = CentreY;
            float halfW = (Nx - 1) * Sx / 2, halfH = (Ny - 1) * Sy / 2;
            cx = Math.Max(roomLeft + halfW, Math.Min(roomRight - halfW, cx));
            cy = Math.Max(roomTop + halfH, Math.Min(roomBottom - halfH, cy));
            _fromX = (float[])X.Clone(); _fromY = (float[])Y.Clone(); _fromZ = (float[])Z.Clone();
            _toX = new float[Count]; _toY = new float[Count];
            for (int i = 0; i < Count; i++) { _toX[i] = cx + _offX[i]; _toY[i] = cy + _offY[i]; }
        }

        /// <summary>t from 0 to 1.</summary>
        public void Flatten(float t)
        {
            float e = t * t * (3 - 2 * t);
            for (int i = 0; i < Count; i++)
            {
                X[i] = _fromX[i] + (_toX[i] - _fromX[i]) * e;
                Y[i] = _fromY[i] + (_toY[i] - _fromY[i]) * e;
                Z[i] = _fromZ[i] + (Floor - _fromZ[i]) * e;
                _px[i] = X[i]; _py[i] = Y[i]; _pz[i] = Z[i];
            }
        }

        // ------------------------------------------------------------------ helpers

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
