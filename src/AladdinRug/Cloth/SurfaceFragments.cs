using System;
using System.Windows;

namespace AladdinRug
{
    /// <summary>Reusable picking data for the drawn rug bounds, rather than an entire monitor.</summary>
    internal sealed class SurfaceHitMap
    {
        private int[] _nodes = Array.Empty<int>();
        public Int32Rect Bounds { get; private set; }
        private int Count => Bounds.Width * Bounds.Height;

        private void Reserve(Int32Rect bounds)
        {
            Bounds = bounds;
            if (_nodes.Length < Count) _nodes = new int[checked((Count + 4095) / 4096 * 4096)];
        }

        public void Begin(Int32Rect bounds) { Reserve(bounds); Array.Clear(_nodes, 0, Count); }
        public void Set(int x, int y, int node) => _nodes[(y - Bounds.Y) * Bounds.Width + x - Bounds.X] = node;
        public int NodeAt(int x, int y) => x < Bounds.X || y < Bounds.Y || x >= Bounds.X + Bounds.Width || y >= Bounds.Y + Bounds.Height
            ? -1 : _nodes[(y - Bounds.Y) * Bounds.Width + x - Bounds.X] - 1;
        public void CopyFrom(SurfaceHitMap source) { Reserve(source.Bounds); Array.Copy(source._nodes, _nodes, Count); }
    }

    /// <summary>One reusable scan band. Sort each pixel's translucent fragments by actual interpolated depth.</summary>
    internal sealed class SurfaceFragments
    {
        private struct Fragment { public float Z, B, G, R, A; public int Next, Node; }
        private int[] _heads = Array.Empty<int>();
        private Fragment[] _items = new Fragment[256];
        private int _count, _width, _row, _pixels;

        public void Begin(int width, int row, int rows)
        {
            _width = width; _row = row; _pixels = width * rows; _count = 0;
            if (_heads.Length < _pixels) _heads = new int[_pixels];
            Array.Fill(_heads, -1, 0, _pixels);
        }

        public void Add(int x, int y, float z, float b, float g, float r, float a, int node)
        {
            if (_count == _items.Length) Array.Resize(ref _items, checked(_items.Length * 2));
            int pixel = (y - _row) * _width + x, previous = -1, next = _heads[pixel];
            while (next >= 0 && _items[next].Z <= z) { previous = next; next = _items[next].Next; }
            int index = _count++;
            _items[index] = new Fragment { Z = z, B = b, G = g, R = r, A = a, Node = node, Next = next };
            if (previous < 0) _heads[pixel] = index;
            else _items[previous].Next = index;
        }

        public void Composite(byte[] pixels, SurfaceHitMap hitMap)
        {
            for (int k = 0; k < _pixels; k++)
            {
                int index = _heads[k];
                if (index < 0) continue;
                float b = 0, g = 0, r = 0, a = 0;
                int node = 0;
                for (; index >= 0; index = _items[index].Next)
                {
                    ref Fragment f = ref _items[index];
                    float keep = 1 - f.A / 255f;
                    b = f.B + b * keep; g = f.G + g * keep; r = f.R + r * keep; a = f.A + a * keep;
                    node = f.Node + 1; // zero means no visible surface; transparent holes never enter this list
                }
                int p = _row * _width + k, dst = p * 4;
                pixels[dst] = (byte)Math.Clamp(b, 0, 255); pixels[dst + 1] = (byte)Math.Clamp(g, 0, 255);
                pixels[dst + 2] = (byte)Math.Clamp(r, 0, 255); pixels[dst + 3] = (byte)Math.Clamp(a, 0, 255);
                hitMap?.Set(k % _width, _row + k / _width, node);
            }
        }
    }
}
