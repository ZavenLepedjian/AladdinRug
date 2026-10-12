using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace AladdinRug
{
    /// <summary>
    /// Triangle surface contacts and conservative continuous collision detection.
    /// The saved start positions are independent of the Verlet velocity history.
    /// A proposed move is accepted only as far as every nonadjacent triangle pair can safely travel.
    /// </summary>
    internal sealed class ClothCollision
    {
        internal const float Clearance = 0.10f;
        private const int AdvanceLimit = 32;
        private readonly struct Triangle
        {
            public readonly int A, B, C, Id;
            public Triangle(int a, int b, int c, int id) { A = a; B = b; C = c; Id = id; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Adjacent(Triangle t) => A == t.A || A == t.B || A == t.C || B == t.A || B == t.B || B == t.C || C == t.A || C == t.B || C == t.C;
            public Vector3 Point(Vector3[] points, Vector3 w) => points[A] * w.X + points[B] * w.Y + points[C] * w.Z;
        }
        private struct Bounds { public Vector3 Min, Max; }
        private struct Contact
        {
            public Vector3 P, Q, U, V;
            public float Distance2;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Offer(Vector3 p, Vector3 q, Vector3 u, Vector3 v)
            {
                float d = Vector3.DistanceSquared(p, q);
                if (d >= Distance2) return;
                P = p; Q = q; U = u; V = v; Distance2 = d;
            }
        }
        private readonly Triangle[] _triangles;
        private readonly Vector3[] _start, _end;
        private readonly Vector3[] _motion;
        private readonly bool[] _moving;
        private Bounds[] _bounds;
        private Bounds[] _fatBounds;
        private struct Node { public Bounds Box; public int Left, Right, Triangle; }
        private readonly Node[] _nodes;
        private int _nextNode;
        private long[] _candidateKeys = new long[1024];
        private int _candidateCount;
        private int[] _pairA = new int[128], _pairB = new int[128];
        private readonly bool _cacheCandidates;
        private long[] _cachedKeys = Array.Empty<long>();
        private int _cachedCount;
        private bool _cacheValid;
        private struct CellSpan { public int X0, Y0, Z0, X1, Y1, Z1; }
        private readonly CellSpan[] _spans;
        private readonly int[] _leafOfTriangle;
        private int[] _heads = Array.Empty<int>(), _items = Array.Empty<int>(), _next = Array.Empty<int>();
        private readonly int[][] _gridSeen, _gridGroups;
        private readonly int[] _gridCounts;
        private readonly Action<int> _gridAction;
        private int _gridNx, _gridNy;
        private readonly int _workers;
        private readonly float[] _fractions;
        private readonly Action<int> _acceptAction;
        private readonly ParallelOptions _parallel;
        public ClothCollision(int nx, int ny) : this(nx * ny, GridIndices(nx, ny), nx) { }

        // Small explicit surfaces are used by the synthetic developer checks.
        internal ClothCollision(int vertexCount, int[] indices) : this(vertexCount, indices, vertexCount) { }

        private ClothCollision(int vertexCount, int[] indices, int gridWidth)
        {
            _start = new Vector3[vertexCount]; _end = new Vector3[vertexCount];
            _triangles = new Triangle[indices.Length / 3];
            for (int i = 0; i < _triangles.Length; i++)
                _triangles[i] = new Triangle(indices[i * 3], indices[i * 3 + 1], indices[i * 3 + 2], i);
            int n = _triangles.Length;
            _bounds = new Bounds[n]; _spans = new CellSpan[n]; _leafOfTriangle = new int[n]; _nodes = MakeTree(gridWidth);
            _cacheCandidates = n < 4096;
            _workers = n < 256 ? 1 : Math.Min(4, Math.Max(1, Environment.ProcessorCount - 1));
            _fractions = new float[_workers]; _acceptAction = GatherAcceptance;
            _motion = new Vector3[vertexCount]; _moving = new bool[n];
            _parallel = new ParallelOptions { MaxDegreeOfParallelism = _workers };
            _gridSeen = new int[_workers][];
            for (int i = 0; i < _workers; i++) _gridSeen[i] = new int[n];
            _gridGroups = new int[n][]; _gridCounts = new int[n]; _gridAction = GatherGridCandidates;
        }

        private static int[] GridIndices(int nx, int ny)
        {
            var indices = new int[6 * (nx - 1) * (ny - 1)];
            int n = 0;
            for (int j = 0; j < ny - 1; j++) for (int i = 0; i < nx - 1; i++)
            {
                int k = j * nx + i;
                indices[n++] = k; indices[n++] = k + 1; indices[n++] = k + nx;
                indices[n++] = k + 1; indices[n++] = k + nx + 1; indices[n++] = k + nx;
            }
            return indices;
        }

        public void Begin(float[] x, float[] y, float[] z) => Read(_start, x, y, z);
        private static void Read(Vector3[] dst, float[] x, float[] y, float[] z)
        { for (int i = 0; i < dst.Length; i++) dst[i] = new Vector3(x[i], y[i], z[i]); }
        private static void Write(Vector3[] src, float[] x, float[] y, float[] z)
        { for (int i = 0; i < src.Length; i++) { x[i] = src[i].X; y[i] = src[i].Y; z[i] = src[i].Z; } }

        // Fixed material-space hierarchy, refitted after every positional projection. Folded pieces
        // stay in different branches, but their current/swept boxes still discover each other.
        private Node[] MakeTree(int nx)
        {
            var nodes = new Node[_triangles.Length * 2 - 1];
            var order = new int[_triangles.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            float Coordinate(int i, bool y)
            {
                Triangle q = _triangles[i];
                return y ? q.A / nx + q.B / nx + q.C / nx : q.A % nx + q.B % nx + q.C % nx;
            }
            int Add(int first, int count)
            {
                int index = _nextNode++;
                if (count == 1) { nodes[index].Triangle = order[first]; _leafOfTriangle[order[first]] = index; return index; }
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = first; i < first + count; i++)
                {
                    float x = Coordinate(order[i], false), y = Coordinate(order[i], true);
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                bool axisY = maxY - minY > maxX - minX;
                Array.Sort(order, first, count, System.Collections.Generic.Comparer<int>.Create((a, b) => Coordinate(a, axisY).CompareTo(Coordinate(b, axisY))));
                int left = Add(first, count / 2), right = Add(first + count / 2, count - count / 2);
                nodes[index].Left = left; nodes[index].Right = right; nodes[index].Triangle = -1;
                return index;
            }
            Add(0, order.Length);
            return nodes;
        }

        private void Build(bool swept, float inflation)
        {
            var pad = new Vector3(inflation);
            for (int i = 0; i < _triangles.Length; i++)
            {
                Triangle t = _triangles[i];
                Vector3 min = Vector3.Min(_end[t.A], Vector3.Min(_end[t.B], _end[t.C]));
                Vector3 max = Vector3.Max(_end[t.A], Vector3.Max(_end[t.B], _end[t.C]));
                if (swept)
                {
                    min = Vector3.Min(min, Vector3.Min(_start[t.A], Vector3.Min(_start[t.B], _start[t.C])));
                    max = Vector3.Max(max, Vector3.Max(_start[t.A], Vector3.Max(_start[t.B], _start[t.C])));
                }
                _bounds[i] = new Bounds { Min = min - pad, Max = max + pad };
            }
            RefitTree();
        }
        private void RefitTree()
        {
            for (int i = _nodes.Length - 1; i >= 0; i--)
            {
                ref Node n = ref _nodes[i];
                n.Box = n.Triangle >= 0 ? _bounds[n.Triangle] : new Bounds
                {
                    Min = Vector3.Min(_nodes[n.Left].Box.Min, _nodes[n.Right].Box.Min),
                    Max = Vector3.Max(_nodes[n.Left].Box.Max, _nodes[n.Right].Box.Max)
                };
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Overlap(Bounds a, Bounds b) => a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
            a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

        // Emit each overlapping leaf pair once, then preserve the original Gauss-Seidel order:
        // ascending first triangle, descending second leaf, independent of the worker count.
        private void FindCandidates()
        {
            if (_cacheCandidates) FindCachedCandidates();
            else FindUncachedCandidates();
        }

        // Reuse a superset only while every current/swept box is contained in its saved fat box.
        // Filtering that ordered superset by the fresh exact boxes yields the same pair sequence.
        // Any escaped vertex/box rebuilds the entire set before it can be used by projection or CCD.
        private void FindCachedCandidates()
        {
            const float padding = 5;
            if (_cacheValid)
                for (int i = 0; i < _bounds.Length; i++)
                {
                    Bounds b = _bounds[i], fat = _fatBounds[i];
                    if (!(b.Min.X >= fat.Min.X && b.Min.Y >= fat.Min.Y && b.Min.Z >= fat.Min.Z && b.Max.X <= fat.Max.X && b.Max.Y <= fat.Max.Y && b.Max.Z <= fat.Max.Z))
                    { _cacheValid = false; break; }
                }
            if (!_cacheValid)
            {
                _fatBounds ??= new Bounds[_bounds.Length];
                var pad = new Vector3(padding);
                for (int i = 0; i < _bounds.Length; i++) _fatBounds[i] = new Bounds { Min = _bounds[i].Min - pad, Max = _bounds[i].Max + pad };
                Bounds[] exact = _bounds; _bounds = _fatBounds;
                try
                {
                    RefitTree(); FindUncachedCandidates();
                    if (_cachedKeys.Length < _candidateCount) Array.Resize(ref _cachedKeys, Math.Max(_candidateCount, _cachedKeys.Length * 2));
                    _cachedCount = _candidateCount; Array.Copy(_candidateKeys, _cachedKeys, _cachedCount); _cacheValid = true;
                }
                finally { _bounds = exact; RefitTree(); }
            }
            _candidateCount = 0;
            for (int i = 0; i < _cachedCount; i++)
            {
                long key = _cachedKeys[i]; int a = (int)(key >> 32), b = _nodes[int.MaxValue - (int)(uint)key].Triangle;
                if (Overlap(_bounds[a], _bounds[b])) _candidateKeys[_candidateCount++] = key;
            }
        }
        private void FindUncachedCandidates()
        {
            _candidateCount = 0;
            if (!FindGridCandidates())
            {
                int size = 1; _pairA[0] = _pairB[0] = 0;
                void Push(int a, int b)
                {
                    if (size == _pairA.Length) { Array.Resize(ref _pairA, size * 2); Array.Resize(ref _pairB, size * 2); }
                    _pairA[size] = a; _pairB[size++] = b;
                }
                while (size > 0)
                {
                    int aIndex = _pairA[--size], bIndex = _pairB[size];
                    ref Node a = ref _nodes[aIndex]; ref Node b = ref _nodes[bIndex];
                    if (aIndex == bIndex)
                    {
                        if (a.Triangle >= 0) continue;
                        Push(a.Left, a.Left); Push(a.Left, a.Right); Push(a.Right, a.Right); continue;
                    }
                    if (!Overlap(a.Box, b.Box)) continue;
                    if (a.Triangle >= 0 && b.Triangle >= 0)
                    {
                        if (_triangles[a.Triangle].Adjacent(_triangles[b.Triangle])) continue;
                        if (a.Triangle < b.Triangle) AddCandidate(a.Triangle, bIndex);
                        else AddCandidate(b.Triangle, aIndex);
                    }
                    else if (b.Triangle >= 0 || (a.Triangle < 0 && Extent(a.Box) >= Extent(b.Box)))
                    { Push(a.Left, bIndex); Push(a.Right, bIndex); }
                    else { Push(aIndex, b.Left); Push(aIndex, b.Right); }
                }
                Array.Sort(_candidateKeys, 0, _candidateCount);
            }
        }
        // A uniform grid is only a broadphase: exact inflated/swept bounds are still checked.
        // Large/exceptional spans fall back to the tree; no candidate can be dropped for a budget.
        private bool FindGridCandidates()
        {
            const float cell = 24;
            Vector3 origin = _nodes[0].Box.Min, extent = _nodes[0].Box.Max - origin;
            if (!float.IsFinite(extent.X + extent.Y + extent.Z)) return false;
            double dx = Math.Ceiling(extent.X / cell) + 1, dy = Math.Ceiling(extent.Y / cell) + 1, dz = Math.Ceiling(extent.Z / cell) + 1;
            if (dx < 1 || dy < 1 || dz < 1 || dx * dy * dz > Math.Max(4096, 32L * _triangles.Length)) return false;
            int nx = (int)dx, ny = (int)dy, nz = (int)dz, cells = nx * ny * nz, entries = 0;
            for (int i = 0; i < _bounds.Length; i++)
            {
                Bounds b = _bounds[i];
                var span = new CellSpan
                {
                    X0 = Math.Clamp((int)MathF.Floor((b.Min.X - origin.X) / cell), 0, nx - 1),
                    X1 = Math.Clamp((int)MathF.Floor((b.Max.X - origin.X) / cell), 0, nx - 1),
                    Y0 = Math.Clamp((int)MathF.Floor((b.Min.Y - origin.Y) / cell), 0, ny - 1),
                    Y1 = Math.Clamp((int)MathF.Floor((b.Max.Y - origin.Y) / cell), 0, ny - 1),
                    Z0 = Math.Clamp((int)MathF.Floor((b.Min.Z - origin.Z) / cell), 0, nz - 1),
                    Z1 = Math.Clamp((int)MathF.Floor((b.Max.Z - origin.Z) / cell), 0, nz - 1)
                };
                long count = (long)(span.X1 - span.X0 + 1) * (span.Y1 - span.Y0 + 1) * (span.Z1 - span.Z0 + 1);
                if (count + entries > 64L * _triangles.Length) return false;
                entries += (int)count; _spans[i] = span;
            }
            if (_heads.Length < cells) Array.Resize(ref _heads, cells);
            if (_items.Length < entries) { int length = Math.Max(entries, _items.Length * 2); Array.Resize(ref _items, length); Array.Resize(ref _next, length); }
            Array.Fill(_heads, -1, 0, cells); int entry = 0;
            for (int i = 0; i < _spans.Length; i++)
            {
                CellSpan s = _spans[i];
                for (int z = s.Z0; z <= s.Z1; z++) for (int y = s.Y0; y <= s.Y1; y++) for (int x = s.X0; x <= s.X1; x++)
                {
                    int index = (z * ny + y) * nx + x; _items[entry] = i; _next[entry] = _heads[index]; _heads[index] = entry++;
                }
            }
            _gridNx = nx; _gridNy = ny;
            if (_workers > 1) Parallel.For(0, _workers, _parallel, _gridAction);
            else for (int worker = 0; worker < _workers; worker++) GatherGridCandidates(worker);
            for (int ia = 0; ia < _spans.Length; ia++)
                for (int n = 0; n < _gridCounts[ia]; n++) AddCandidate(ia, _gridGroups[ia][n]);
            return true;
        }
        // Each worker owns complete first-triangle groups and its duplicate stamps. Joining
        // groups in triangle order gives stable contact ordering before any position changes.
        private void GatherGridCandidates(int worker)
        {
            int[] seen = _gridSeen[worker]; Array.Clear(seen); int nx = _gridNx, ny = _gridNy;
            int begin = _spans.Length * worker / _workers, end = _spans.Length * (worker + 1) / _workers;
            for (int ia = begin; ia < end; ia++)
            {
                CellSpan s = _spans[ia]; Triangle a = _triangles[ia]; Bounds box = _bounds[ia]; int stamp = ia + 1, count = 0;
                int[] group = _gridGroups[ia];
                for (int z = s.Z0; z <= s.Z1; z++) for (int y = s.Y0; y <= s.Y1; y++) for (int x = s.X0; x <= s.X1; x++)
                    for (int n = _heads[(z * ny + y) * nx + x]; n >= 0; n = _next[n])
                    {
                        // Cells are filled in ascending triangle order and prepended to their list.
                        // All remaining entries after this one therefore have an equal or smaller id.
                        int ib = _items[n]; if (ib <= ia) break; if (seen[ib] == stamp) continue; seen[ib] = stamp;
                        if (a.Adjacent(_triangles[ib]) || !Overlap(box, _bounds[ib])) continue;
                        if (group == null) group = new int[16]; else if (count == group.Length) Array.Resize(ref group, count * 2);
                        group[count++] = _leafOfTriangle[ib];
                    }
                for (int k = 1; k < count; k++)
                { int leaf = group[k], j = k - 1; while (j >= 0 && group[j] < leaf) { group[j + 1] = group[j]; j--; } group[j + 1] = leaf; }
                _gridGroups[ia] = group; _gridCounts[ia] = count;
            }
        }

        private static float Extent(Bounds b) { Vector3 v = b.Max - b.Min; return v.X + v.Y + v.Z; }
        private void AddCandidate(int first, int secondLeaf)
        {
            if (_candidateCount == _candidateKeys.Length) Array.Resize(ref _candidateKeys, _candidateCount * 2);
            _candidateKeys[_candidateCount++] = ((long)first << 32) | (uint)(int.MaxValue - secondLeaf);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Pair(int index, out Triangle a, out Triangle b)
        {
            long key = _candidateKeys[index];
            a = _triangles[(int)(key >> 32)]; b = _triangles[_nodes[int.MaxValue - (int)(uint)key].Triangle];
        }
        /// <summary>Resolve surface contacts sequentially; each correction sees prior position changes.</summary>
        public void Project(float[] x, float[] y, float[] z, float[] inverseMass, float thickness)
        {
            Read(_end, x, y, z); Build(false, thickness * .5f);
            FindCandidates();
            for (int i = 0; i < _candidateCount; i++)
            { Pair(i, out Triangle a, out Triangle b); ProjectPair(a, b, inverseMass, thickness); }
            Write(_end, x, y, z);
        }
        private void ProjectPair(Triangle a, Triangle b, float[] inverseMass, float thickness)
        {
            if (Apart(a, b, 1, thickness)) return;
            Contact c = Closest(a, b, 1, true);
            if (c.Distance2 >= thickness * thickness) return;
            Vector3 delta = c.P - c.Q, previous = a.Point(_start, c.U) - b.Point(_start, c.V);
            float distance = MathF.Sqrt(c.Distance2); Vector3 normal;
            if (previous.LengthSquared() > 1e-8f && (distance < 1e-5f || Vector3.Dot(previous, delta) < 0)) normal = Vector3.Normalize(previous);
            else if (distance > 1e-5f) normal = delta / distance;
            else
            {
                normal = Vector3.Cross(_start[a.B] - _start[a.A], _start[a.C] - _start[a.A]);
                if (normal.LengthSquared() < 1e-8f) return; normal = Vector3.Normalize(normal);
            }
            float weight = Weight(a, c.U, inverseMass) + Weight(b, c.V, inverseMass);
            if (weight <= 1e-8f) return;
            float correction = Math.Min(thickness, thickness - Vector3.Dot(delta, normal)) / weight;
            Apply(a, c.U, normal * correction, inverseMass); Apply(b, c.V, -normal * correction, inverseMass);
        }
        private void GatherAcceptance(int worker)
        {
            float accepted = 1;
            for (int i = worker; i < _candidateCount && accepted > 0; i += _workers)
            {
                Pair(i, out Triangle a, out Triangle b);
                accepted = Math.Min(accepted, SafeFraction(a, b, accepted));
            }
            _fractions[worker] = accepted;
        }
        private static float Weight(Triangle t, Vector3 w, float[] mass) => mass[t.A] * w.X * w.X + mass[t.B] * w.Y * w.Y + mass[t.C] * w.Z * w.Z;
        private void Apply(Triangle t, Vector3 w, Vector3 correction, float[] mass)
        {
            _end[t.A] += correction * (mass[t.A] * w.X); _end[t.B] += correction * (mass[t.B] * w.Y); _end[t.C] += correction * (mass[t.C] * w.Z);
        }

        /// <summary>
        /// Conservative advancement of both moving surfaces. Distance can shrink no faster than the relative
        /// vertex-speed bound. This is not endpoint testing or fixed-time sampling. Exhausting the iteration
        /// budget accepts only the already-proven safe prefix, rather than skipping an unresolved contact.
        /// </summary>
        public float LimitMotion(float[] x, float[] y, float[] z)
        {
            Read(_end, x, y, z);
            for (int i = 0; i < _motion.Length; i++) _motion[i] = _end[i] - _start[i];
            for (int i = 0; i < _triangles.Length; i++) { Triangle t = _triangles[i]; _moving[i] = _motion[t.A] != Vector3.Zero || _motion[t.B] != Vector3.Zero || _motion[t.C] != Vector3.Zero; }
            Build(true, Clearance * 0.5f); FindCandidates();
            if (_workers == 1) GatherAcceptance(0); else Parallel.For(0, _workers, _parallel, _acceptAction);
            float accepted = 1;
            for (int i = 0; i < _workers; i++) accepted = Math.Min(accepted, _fractions[i]);
            if (accepted < 1)
            {
                // Leave room for roundoff; all pairs were proven safe for this common prefix.
                accepted *= 0.98f;
                for (int i = 0; i < _end.Length; i++) _end[i] = Vector3.Lerp(_start[i], _end[i], accepted);
                Write(_end, x, y, z);
            }
            return accepted;
        }
        private float SafeFraction(Triangle a, Triangle b, float limit)
        {
            if (!_moving[a.Id] && !_moving[b.Id]) return limit;
            Vector3 common = (_motion[a.A] + _motion[b.A]) * .5f;
            float speed = MaxSpeed(a, common) + MaxSpeed(b, common);
            if (speed < 1e-7f) return limit;
            float time = 0;
            for (int iteration = 0; iteration < AdvanceLimit; iteration++)
            {
                if (Apart(a, b, time, Clearance + speed * (limit - time))) return limit;
                Contact closest = Closest(a, b, time, false);
                float distance = MathF.Sqrt(closest.Distance2);
                if (time == 0 && distance <= Clearance * 1.1f && distance > 1e-6f &&
                    SeparatedPath(a, b, (closest.P - closest.Q) / distance, limit)) return limit;
                float travel = (distance - Clearance) / speed;
                if (travel >= limit - time) return limit;
                if (travel <= 1e-6f) return time;
                time += travel * 0.9f;
            }
            return time;
        }
        // At an existing contact the advancement guard has no time left, even when moving away.
        // A fixed separating axis at BOTH ends proves separation for the entire linear path:
        // every projected vertex-pair gap is linear and stays above its smaller endpoint gap.
        // This is a continuous certificate, not a permission to skip near-contact CCD.
        private bool SeparatedPath(Triangle a, Triangle b, Vector3 axis, float limit)
        {
            return ProjectedGap(a, b, axis, 0) > .01f && ProjectedGap(a, b, axis, limit) > .01f;
        }
        private float ProjectedGap(Triangle a, Triangle b, Vector3 axis, float time)
        {
            Vector3 origin = At(b.A, time);
            float Project(int i) => Vector3.Dot(At(i, time) - origin, axis);
            return Math.Min(Project(a.A), Math.Min(Project(a.B), Project(a.C))) - Math.Max(0, Math.Max(Project(b.B), Project(b.C)));
        }

        private float MaxSpeed(Triangle t, Vector3 common) => MathF.Sqrt(Math.Max(
            (_motion[t.A] - common).LengthSquared(), Math.Max(
            (_motion[t.B] - common).LengthSquared(), (_motion[t.C] - common).LengthSquared())));

        /// <summary>Geometric surface intersections, independent of the old point-pair diagnostic.</summary>
        public int Intersections(float[] x, float[] y, float[] z)
        {
            Read(_end, x, y, z); Build(false, 0);
            FindCandidates(); int count = 0;
            for (int i = 0; i < _candidateCount; i++)
            {
                Pair(i, out Triangle a, out Triangle b);
                if (Closest(a, b, 1, true).Distance2 < 1e-8f) count++;
            }
            return count;
        }

        // A separating projection gives a conservative lower bound on distance. Usually this
        // rejects nearby bounding boxes without evaluating all six vertex/face and nine edge pairs.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private Vector3 At(int index, float time) => time == 0 ? _start[index] : time == 1 ? _end[index] : Vector3.Lerp(_start[index], _end[index], time);
        private bool Apart(Triangle a, Triangle b, float time, float margin)
        {
            Vector3 a0 = At(a.A, time), a1 = At(a.B, time), a2 = At(a.C, time);
            Vector3 b0 = At(b.A, time), b1 = At(b.B, time), b2 = At(b.C, time);
            if (AxisApart((a0 - b0) + (a1 - b1) + (a2 - b2), a0, a1, a2, b0, b1, b2, margin)) return true;
            Vector3 n = Vector3.Cross(a1 - a0, a2 - a0);
            if (AxisApart(n, a0, a1, a2, b0, b1, b2, margin)) return true;
            Vector3 m = Vector3.Cross(b1 - b0, b2 - b0);
            return AxisApart(m, a0, a1, a2, b0, b1, b2, margin) ||
                AxisApart(Vector3.Cross(n, a1 - a0), a0, a1, a2, b0, b1, b2, margin) ||
                AxisApart(Vector3.Cross(n, a2 - a1), a0, a1, a2, b0, b1, b2, margin) ||
                AxisApart(Vector3.Cross(n, a0 - a2), a0, a1, a2, b0, b1, b2, margin) ||
                AxisApart(Vector3.Cross(m, b1 - b0), a0, a1, a2, b0, b1, b2, margin) ||
                AxisApart(Vector3.Cross(m, b2 - b1), a0, a1, a2, b0, b1, b2, margin) ||
                AxisApart(Vector3.Cross(m, b0 - b2), a0, a1, a2, b0, b1, b2, margin);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AxisApart(Vector3 axis, Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2, float margin)
        {
            float p = Vector3.Dot(axis, a0 - b0), q = Vector3.Dot(axis, a1 - b0), r = Vector3.Dot(axis, a2 - b0);
            float u = Vector3.Dot(axis, b1 - b0), v = Vector3.Dot(axis, b2 - b0);
            float gap = Math.Max(Math.Min(p, Math.Min(q, r)) - Math.Max(0, Math.Max(u, v)), Math.Min(0, Math.Min(u, v)) - Math.Max(p, Math.Max(q, r)));
            // Include a roundoff allowance; an inconclusive axis always falls back to exact features.
            margin += .002f;
            return gap > 0 && gap * gap > margin * margin * axis.LengthSquared();
        }
        public float AcceptedDistance(float[] x, float[] y, float[] z, float[] inverseMass)
        {
            float sum = 0; int count = 0;
            for (int i = 0; i < _start.Length; i++) if (inverseMass[i] > 0)
            { sum += Vector3.Distance(_start[i], new Vector3(x[i], y[i], z[i])); count++; }
            return count == 0 ? 0 : sum / count;
        }

        private Contact Closest(Triangle a, Triangle b, float time, bool intersections)
        {
            Vector3 a0 = At(a.A, time), a1 = At(a.B, time), a2 = At(a.C, time);
            Vector3 b0 = At(b.A, time), b1 = At(b.B, time), b2 = At(b.C, time);
            return Closest(a0, a1, a2, b0, b1, b2, intersections);
        }
        private static readonly Vector3 U0 = new Vector3(1, 0, 0), U1 = new Vector3(0, 1, 0), U2 = new Vector3(0, 0, 1);
        private static Contact Closest(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2, bool intersections)
        {
            var c = new Contact { Distance2 = float.MaxValue };
            if (intersections && PlaneStraddles(a0, a1, a2, b0, b1, b2) && PlaneStraddles(b0, b1, b2, a0, a1, a2))
            {
                if (Crossing(a0, a1, b0, b1, b2, out float t, out Vector3 w)) return Hit(a0, a1, t, Vector3.Lerp(U0, U1, t), w);
                if (Crossing(a1, a2, b0, b1, b2, out t, out w)) return Hit(a1, a2, t, Vector3.Lerp(U1, U2, t), w);
                if (Crossing(a2, a0, b0, b1, b2, out t, out w)) return Hit(a2, a0, t, Vector3.Lerp(U2, U0, t), w);
                if (Crossing(b0, b1, a0, a1, a2, out t, out w)) return Hit(b0, b1, t, w, Vector3.Lerp(U0, U1, t));
                if (Crossing(b1, b2, a0, a1, a2, out t, out w)) return Hit(b1, b2, t, w, Vector3.Lerp(U1, U2, t));
                if (Crossing(b2, b0, a0, a1, a2, out t, out w)) return Hit(b2, b0, t, w, Vector3.Lerp(U2, U0, t));
            }
            var faceA = new Face(a0, a1, a2); var faceB = new Face(b0, b1, b2);
            Point(a0, U0, in faceB, false, ref c); Point(a1, U1, in faceB, false, ref c); Point(a2, U2, in faceB, false, ref c);
            Point(b0, U0, in faceA, true, ref c); Point(b1, U1, in faceA, true, ref c); Point(b2, U2, in faceA, true, ref c);
            var a01 = new Segment(a0, a1, U0, U1); var a12 = new Segment(a1, a2, U1, U2); var a20 = new Segment(a2, a0, U2, U0);
            var b01 = new Segment(b0, b1, U0, U1); var b12 = new Segment(b1, b2, U1, U2); var b20 = new Segment(b2, b0, U2, U0);
            EdgeRow(in a01, in b01, in b12, in b20, ref c); EdgeRow(in a12, in b01, in b12, in b20, ref c); EdgeRow(in a20, in b01, in b12, in b20, ref c);
            return c;
        }
        private static Contact Hit(Vector3 p, Vector3 q, float t, Vector3 u, Vector3 v) => new Contact { P = Vector3.Lerp(p, q, t), Q = Vector3.Lerp(p, q, t), U = u, V = v, Distance2 = 0 };
        private static bool Crossing(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c, out float t, out Vector3 weights)
        {
            t = 0; weights = default;
            Vector3 d = q - p, e1 = b - a, e2 = c - a, h = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, h);
            if (Math.Abs(det) < 1e-8f) return false;
            Vector3 s = p - a; float u = Vector3.Dot(s, h) / det;
            if (u < 0 || u > 1) return false;
            Vector3 r = Vector3.Cross(s, e1); float v = Vector3.Dot(d, r) / det;
            if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, r) / det;
            if (t < 0 || t > 1) return false;
            weights = new Vector3(1 - u - v, u, v); return true;
        }
        private readonly struct Face
        {
            public readonly Vector3 P, AB, AC;
            public readonly float AA, BB, ABAC, Denominator;
            public Face(Vector3 a, Vector3 b, Vector3 c)
            { P = a; AB = b - a; AC = c - a; AA = Vector3.Dot(AB, AB); BB = Vector3.Dot(AC, AC); ABAC = Vector3.Dot(AB, AC); Denominator = AA * BB - ABAC * ABAC; }
        }
        private static void Point(Vector3 p, Vector3 source, in Face face, bool swap, ref Contact result)
        {
            if (face.Denominator > 1e-10f)
            {
                Vector3 ap = p - face.P; float ab = Vector3.Dot(ap, face.AB), ac = Vector3.Dot(ap, face.AC);
                float u = (face.BB * ab - face.ABAC * ac) / face.Denominator, v = (face.AA * ac - face.ABAC * ab) / face.Denominator;
                if (u >= 0 && v >= 0 && u + v <= 1)
                {
                    Vector3 q = face.P + face.AB * u + face.AC * v, w = new Vector3(1 - u - v, u, v);
                    if (swap) result.Offer(q, p, w, source); else result.Offer(p, q, source, w);
                }
            }
            // Outside-face and degenerate cases are covered by all nine edge/edge distances.
        }
        private static bool PlaneStraddles(Vector3 a, Vector3 b, Vector3 c, Vector3 p, Vector3 q, Vector3 r)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            float x = Vector3.Dot(p - a, n), y = Vector3.Dot(q - a, n), z = Vector3.Dot(r - a, n);
            float tolerance = .0001f * n.Length();
            return Math.Min(x, Math.Min(y, z)) <= tolerance && Math.Max(x, Math.Max(y, z)) >= -tolerance;
        }

        private readonly struct Segment
        {
            public readonly Vector3 P, D, U, V;
            public readonly float Length2;
            public Segment(Vector3 p, Vector3 q, Vector3 u, Vector3 v) { P = p; D = q - p; U = u; V = v; Length2 = Vector3.Dot(D, D); }
        }
        private static void EdgeRow(in Segment a, in Segment b0, in Segment b1, in Segment b2, ref Contact contact)
        { Edge(in a, in b0, ref contact); Edge(in a, in b1, ref contact); Edge(in a, in b2, ref contact); }
        private static void Edge(in Segment firstEdge, in Segment secondEdge, ref Contact c)
        {
            Vector3 d1 = firstEdge.D, d2 = secondEdge.D, delta = firstEdge.P - secondEdge.P;
            float a = firstEdge.Length2, e = secondEdge.Length2, f = Vector3.Dot(d2, delta), x, y;
            if (a < 1e-12f) { x = 0; y = e < 1e-12f ? 0 : Math.Clamp(f / e, 0, 1); }
            else
            {
                float d = Vector3.Dot(d1, delta);
                if (e < 1e-12f) { y = 0; x = Math.Clamp(-d / a, 0, 1); }
                else
                {
                    float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                    x = denom > 1e-12f ? Math.Clamp((b * f - d * e) / denom, 0, 1) : 0;
                    y = (b * x + f) / e;
                    if (y < 0) { y = 0; x = Math.Clamp(-d / a, 0, 1); }
                    else if (y > 1) { y = 1; x = Math.Clamp((b - d) / a, 0, 1); }
                }
            }
            Vector3 first = firstEdge.P + d1 * x, second = secondEdge.P + d2 * y;
            float distance = Vector3.DistanceSquared(first, second);
            if (!(distance >= c.Distance2))
            {
                c.P = first; c.Q = second; c.U = Vector3.Lerp(firstEdge.U, firstEdge.V, x); c.V = Vector3.Lerp(secondEdge.U, secondEdge.V, y); c.Distance2 = distance;
            }
        }
    }
}
