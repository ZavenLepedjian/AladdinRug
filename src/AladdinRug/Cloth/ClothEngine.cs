using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AladdinRug
{
    /// <summary>
    /// Runs the cloth rug off the window's thread, so dragging stays smooth even when the machine is busy:
    ///  - a physics thread steps the cloth at a steady 60 times a second, and sleeps when the rug is still;
    ///  - a drawing thread turns the latest positions into a picture as fast as it can;
    ///  - the window's own thread only copies finished pictures to the screen (and handles the mouse).
    /// Three picture buffers rotate between drawing, waiting and uploading, so nobody waits for anybody else.
    /// </summary>
    internal sealed class ClothEngine : IDisposable
    {
        private const float Step = 1f / 60f;

        public readonly Cloth Cloth;
        private readonly ClothRenderer _renderer;
        private readonly Dust _dust = new Dust(1f);                     // the cloth works in DIPs, so dust sizes are DIPs too
        private readonly DustView _dustView = new DustView();          // what the drawing thread paints
        private readonly DustView _showView = new DustView();
        private readonly Dispatcher _ui;
        private readonly Action<Int32Rect, byte[]> _upload;       // called on the window's thread: copy this area of this picture to the screen
        private readonly int _count;

        // physics <-> drawing
        private readonly object _clothLock = new object();         // a physics step, or a change from the mouse (grab, release, flatten)
        private readonly object _snapLock = new object();
        private readonly float[] _snapX, _snapY, _snapZ, _drawX, _drawY, _drawZ;
        private bool _snapFresh;

        // drawing <-> window
        private readonly byte[][] _buffers = new byte[3][];
        private readonly Int32Rect[] _drawnInto = new Int32Rect[3];  // where each buffer was last drawn into
        private readonly object _frameLock = new object();
        private int _ready = -1, _uploading = -1, _lastDrawn = -1;
        private Int32Rect _readyBox, _shownBox;
        private int _uploadQueued;

        private readonly Thread _physicsThread, _drawThread;
        private readonly ManualResetEventSlim _awake = new ManualResetEventSlim(false);
        private readonly AutoResetEvent _drawNow = new AutoResetEvent(false);
        private readonly object _wakeLock = new object();
        private bool _pendingWake, _paused;
        private volatile bool _quit;

        /// <summary>Raised on the window's thread when the rug has come to rest.</summary>
        public event Action Settled;

        // measuring
        public List<double> StatPhysics, StatDraw, StatUpload, StatUploadGap;
        private long _lastUploadTick;

        public ClothEngine(Cloth cloth, int w, int h, float scale, byte[] face, byte[] back, int texW, int texH, float texPad,
                           Dispatcher ui, Action<Int32Rect, byte[]> upload)
        {
            Cloth = cloth;
            _ui = ui;
            _upload = upload;
            _count = cloth.Count;
            _renderer = new ClothRenderer(cloth, w, h, scale, face, back, texW, texH, texPad);

            _snapX = new float[_count]; _snapY = new float[_count]; _snapZ = new float[_count];
            _drawX = new float[_count]; _drawY = new float[_count]; _drawZ = new float[_count];
            for (int i = 0; i < _buffers.Length; i++) _buffers[i] = new byte[w * h * 4];

            _physicsThread = new Thread(PhysicsLoop) { IsBackground = true, Name = "Rug physics", Priority = ThreadPriority.AboveNormal };
            _drawThread = new Thread(DrawLoop) { IsBackground = true, Name = "Rug drawing" };
            _physicsThread.Start();
            _drawThread.Start();
        }

        public float CentreX { get { lock (_clothLock) return Cloth.CentreX; } }
        public float CentreY { get { lock (_clothLock) return Cloth.CentreY; } }

        // ------------------------------------------------------------------ what the mouse can do

        /// <summary>Draw the rug as it is right now and put it on screen before returning (so there's no gap when it appears).</summary>
        public void ShowNow()
        {
            lock (_clothLock) Cloth.CopyPositions(_drawX, _drawY, _drawZ);
            _dust.CopyTo(_showView);
            Int32Rect box = _renderer.Draw(_buffers[0], ref _drawnInto[0], _drawX, _drawY, _drawZ, _showView);
            _lastDrawn = 0;
            _upload(Union(_shownBox, box), _buffers[0]);
            _shownBox = box;
        }

        /// <summary>Lumps lying under the rug (see <see cref="BumpField"/>), or null.</summary>
        public void SetBump(float[] shade) => _renderer.Bump = shade;

        public bool Grab(float x, float y)
        {
            bool got;
            lock (_clothLock) got = Cloth.Grab(x, y);
            if (got) Wake();
            return got;
        }

        public void MoveHand(float x, float y)
        {
            Cloth.MoveHand(x, y);
            Wake();
        }

        public void Release()
        {
            lock (_clothLock) Cloth.Release();
            Wake();
        }

        /// <summary>Get the physics going again (it sleeps once the rug has been still for half a second).</summary>
        public void Wake()
        {
            lock (_wakeLock)
            {
                if (_paused) return;
                _pendingWake = true;
                _awake.Set();
            }
        }

        // ------------------------------------------------------------------ laying it flat

        public void BeginFlatten(float roomW, float roomH)
        {
            lock (_wakeLock) { _paused = true; _awake.Reset(); }
            lock (_clothLock) Cloth.BeginFlatten(0, 0, roomW, roomH);
        }

        public void FlattenStep(float t)
        {
            lock (_clothLock) { Cloth.Flatten(t); Cloth.CopyPositions(_snapX, _snapY, _snapZ); }
            lock (_snapLock) _snapFresh = true;
            _drawNow.Set();
        }

        // ------------------------------------------------------------------ physics thread

        private void PhysicsLoop()
        {
            var clock = Stopwatch.StartNew();
            double next = 0;
            int calm = 0;

            while (!_quit)
            {
                _awake.Wait();
                if (_quit) break;

                double now = clock.Elapsed.TotalSeconds;
                if (next < now - 0.1) next = now;                       // woke from a rest, or fell far behind: start afresh
                double wait = next - now;
                if (wait > 0.003) Thread.Sleep((int)((wait - 0.0015) * 1000));
                while (clock.Elapsed.TotalSeconds < next) Thread.SpinWait(40);
                next += Step;

                long t0 = Stopwatch.GetTimestamp();
                bool held;
                float speed;
                lock (_clothLock)
                {
                    Cloth.Step(Step);
                    if (Cloth.ImpactCount > 0) _dust.FromCloth(Cloth);
                    held = Cloth.IsHeld;
                    speed = Cloth.Speed;
                    Cloth.CopyPositions(_snapX, _snapY, _snapZ);
                }
                _dust.Step(Step);
                StatPhysics?.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                lock (_snapLock) _snapFresh = true;
                _drawNow.Set();

                if (held || speed > 0.04f || _dust.Active) { calm = 0; continue; }     // dust still in the air keeps the picture moving
                if (++calm < 30) continue;

                // Still for half a second: stop where it lies, and rest until the mouse touches it again.
                lock (_clothLock)
                {
                    Cloth.Freeze();
                    Cloth.CopyPositions(_snapX, _snapY, _snapZ);
                }
                lock (_snapLock) _snapFresh = true;
                _drawNow.Set();
                lock (_wakeLock)
                {
                    if (_pendingWake) { _pendingWake = false; calm = 0; continue; }   // touched again just now
                    _awake.Reset();
                }
                calm = 0;
                _ui.BeginInvoke(DispatcherPriority.Background, new Action(() => Settled?.Invoke()));
            }
        }

        // ------------------------------------------------------------------ drawing thread

        private void DrawLoop()
        {
            while (true)
            {
                _drawNow.WaitOne();
                if (_quit) break;

                lock (_snapLock)
                {
                    if (!_snapFresh) continue;
                    _snapFresh = false;
                    Array.Copy(_snapX, _drawX, _count);
                    Array.Copy(_snapY, _drawY, _count);
                    Array.Copy(_snapZ, _drawZ, _count);
                }

                int b;
                lock (_frameLock)
                {
                    b = 0;
                    while (b == _ready || b == _uploading) b++;         // any buffer nobody is waiting for or reading
                }

                _dust.CopyTo(_dustView);
                long t0 = Stopwatch.GetTimestamp();
                Int32Rect box = _renderer.Draw(_buffers[b], ref _drawnInto[b], _drawX, _drawY, _drawZ, _dustView);
                StatDraw?.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);

                lock (_frameLock) { _ready = b; _readyBox = box; _lastDrawn = b; }
                if (Interlocked.Exchange(ref _uploadQueued, 1) == 0)
                    _ui.BeginInvoke(DispatcherPriority.Render, new Action(Upload));
            }
        }

        // On the window's thread: copy the newest finished picture to the screen.
        private void Upload()
        {
            Interlocked.Exchange(ref _uploadQueued, 0);
            if (_quit) return;

            int b;
            Int32Rect box;
            lock (_frameLock)
            {
                b = _ready;
                if (b < 0) return;
                box = _readyBox;
                _ready = -1;
                _uploading = b;
            }

            long t0 = Stopwatch.GetTimestamp();
            try
            {
                // what is on screen now is the previous picture, so the area to update covers both
                _upload(Union(_shownBox, box), _buffers[b]);
                _shownBox = box;
            }
            finally
            {
                lock (_frameLock) _uploading = -1;
            }

            if (StatUpload != null)
            {
                long now = Stopwatch.GetTimestamp();
                StatUpload.Add((now - t0) * 1000.0 / Stopwatch.Frequency);
                if (_lastUploadTick != 0) StatUploadGap.Add((now - _lastUploadTick) * 1000.0 / Stopwatch.Frequency);
                _lastUploadTick = now;
            }
        }

        private static Int32Rect Union(Int32Rect a, Int32Rect b)
        {
            if (a.Width <= 0 || a.Height <= 0) return b;
            if (b.Width <= 0 || b.Height <= 0) return a;
            int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
            int x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        }

        public void Dispose()
        {
            _quit = true;
            _awake.Set();
            _drawNow.Set();
            _physicsThread.Join(1000);
            _drawThread.Join(1000);
        }
    }
}
