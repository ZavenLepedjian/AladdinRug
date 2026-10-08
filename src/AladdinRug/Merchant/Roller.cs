using System;
using System.Collections.Generic;
using System.Windows;

namespace AladdinRug
{
    /// <summary>What the rug window tells the merchant each frame: where the roll is (screen pixels) and how far the job has got.</summary>
    internal struct RollView
    {
        public double EdgeX, Radius, Top, Height;     // the roll's centre line, its radius, and its extent along the rug's short side
        public double Fraction;                       // how far through the rolling (0..1)
        public bool JobDone;                          // the rug has finished moving
    }

    /// <summary>
    /// The merchant's job: walk in, crouch at the end of the rug, push the roll along it (or back out) while the rug
    /// follows his hands, tie the straps, clap the dust off, and walk away.
    /// </summary>
    internal sealed class Roller : IDisposable
    {
        private enum Phase { Idle, Arriving, Grabbing, Pushing, Finishing, Leaving }

        private readonly DeskGeometry _geo;
        private readonly double _u;                   // pixels per drawing unit
        private readonly IntPtr _rug;
        private readonly MerchantWindow _win;          // null when only checking his movements, with no window
        public double X => _x;
        public double Y => _y;
        public MerchantPose Pose => new MerchantPose { Heading = _heading, Phase = _walkPhase, Walk = _walk, Lean = _lean, Crouch = _crouch, Reach = _reach };
        private Phase _phase = Phase.Idle;
        private bool _up;                             // rolling up (pushing left) or rolling out (pushing right)
        private double _x, _y, _heading, _walkPhase, _t;
        private double _crouch, _lean, _reach, _walk;
        private List<Point> _path;
        private int _leg;

        public double Unit => _u;
        public bool Active => _phase != Phase.Idle;
        public bool PushRequested { get; private set; }

        public Roller(DeskGeometry geo, IntPtr rugWindow, bool headless = false)
        {
            _geo = geo;
            _u = geo.Scale * 1.7;
            _rug = rugWindow;
            _win = headless ? null : new MerchantWindow(_u);
        }

        private double Speed => 235 * _u;
        private double Gap => 27 * _u;

        public double StandX(RollView v) => _up ? v.EdgeX + v.Radius + Gap : v.EdgeX - v.Radius - Gap;

        /// <summary>He comes in from the edge of the screen to where he will push from.</summary>
        public void Start(bool rollingUp, RollView at)
        {
            _up = rollingUp;
            PushRequested = false;
            Point stand = new Point(StandX(at), at.Top + at.Height * 0.5);
            double pad = 60 * _u;
            Point entry;
            if (_up) entry = new Point(_geo.X + _geo.W + pad, stand.Y);
            else if (stand.X - _geo.X > 150 * _u) entry = new Point(_geo.X - pad, stand.Y);
            else entry = new Point(stand.X, stand.Y < _geo.Y + _geo.H / 2 ? _geo.Y - pad : _geo.Y + _geo.H + pad);

            _x = entry.X; _y = entry.Y;
            _path = new List<Point> { entry, stand };
            _leg = 0;
            _heading = Math.Atan2(stand.Y - entry.Y, stand.X - entry.X) * 180 / Math.PI;
            _crouch = _lean = _reach = 0;
            _phase = Phase.Arriving;
            _t = 0;
            Show();
        }

        public void Cancel()
        {
            _phase = Phase.Idle;
            PushRequested = false;
            _win?.Vanish();
        }

        private double PushHeading => _up ? 180 : 0;

        public void Update(double dt, RollView v)
        {
            if (_phase == Phase.Idle) return;
            _t += dt;

            switch (_phase)
            {
                case Phase.Arriving:
                    if (Follow(dt, 1.0)) { _phase = Phase.Grabbing; _t = 0; }
                    break;

                case Phase.Grabbing:
                    _walk = Math.Max(0, _walk - dt * 6);
                    _heading = TurnTo(_heading, PushHeading, dt * 9);
                    _crouch = Approach(_crouch, 0.55, dt * 3);
                    _lean = Approach(_lean, 1, dt * 3.5);
                    _reach = Approach(_reach, 1, dt * 4);
                    if (_t > 0.55) { _phase = Phase.Pushing; _t = 0; PushRequested = true; }
                    break;

                case Phase.Pushing:
                {
                    // follow the roll: stay behind it, and work along its length to keep it straight, then tie the two straps
                    double f = v.Fraction;
                    double frac = f < 0.8 ? 0.5 + 0.30 * Math.Sin(f / 0.8 * 2 * Math.PI * 1.4) : (_up ? 0.13 + 0.74 * Smooth((f - 0.8) / 0.2) : 0.5);
                    double tx = StandX(v), ty = v.Top + v.Height * frac;
                    double nx = _x + (tx - _x) * Math.Min(1, dt * 16), ny = _y + (ty - _y) * Math.Min(1, dt * 12);
                    double moved = Math.Sqrt((nx - _x) * (nx - _x) + (ny - _y) * (ny - _y));
                    _x = nx; _y = ny;
                    double speed = dt > 0 ? moved / dt : 0;
                    _walk = Approach(_walk, Math.Min(1, speed / (140 * _u)), dt * 8);
                    _walkPhase += moved / (_u * 62) * 2 * Math.PI;
                    _heading = PushHeading;
                    bool tying = _up && f >= 0.8;
                    _crouch = Approach(_crouch, tying ? 0.95 : 0.5, dt * 5);
                    _lean = Approach(_lean, 1, dt * 5);
                    _reach = Approach(_reach, 1, dt * 5);
                    if (v.JobDone) { _phase = Phase.Finishing; _t = 0; }
                    break;
                }

                case Phase.Finishing:
                    _walk = Math.Max(0, _walk - dt * 6);
                    _crouch = Approach(_crouch, 0, dt * 4);
                    _lean = Approach(_lean, 0, dt * 4);
                    _reach = _t < 0.25 ? Approach(_reach, 0.4, dt * 8) : 0.4 + 0.45 * Math.Max(0, Math.Sin((_t - 0.25) * 22));    // clapping the dust from his hands
                    _heading = TurnTo(_heading, PushHeading, dt * 6);
                    if (_t > 0.9)
                    {
                        Point from = new Point(_x, _y), exit = NearestExit(from);
                        _path = new List<Point> { from, exit };
                        _leg = 0;
                        _phase = Phase.Leaving;
                        _t = 0;
                        _reach = 0;
                    }
                    break;

                case Phase.Leaving:
                    _crouch = Approach(_crouch, 0, dt * 4);
                    _lean = Approach(_lean, 0, dt * 4);
                    _reach = Approach(_reach, 0, dt * 6);
                    if (Follow(dt, 1.0)) { _phase = Phase.Idle; _win?.Vanish(); return; }
                    break;
            }

            _win?.Present(_x, _y, Pose, _rug);
        }

        // Walk along the path; true when he gets to the end.
        private bool Follow(double dt, double speedScale)
        {
            Point target = _path[_leg + 1];
            double dx = target.X - _x, dy = target.Y - _y, dist = Math.Sqrt(dx * dx + dy * dy);
            double step = Speed * speedScale * dt * (_phase == Phase.Arriving ? 1 + Math.Min(1, dist / (500 * _u)) * 0.4 : 1);

            _walk = Approach(_walk, 1, dt * 7);
            if (dist > 1e-6) _heading = TurnTo(_heading, Math.Atan2(dy, dx) * 180 / Math.PI, dt * 10);
            _crouch = Approach(_crouch, 0, dt * 4);
            double move = Math.Min(step, dist);
            if (dist > 1e-6) { _x += dx / dist * move; _y += dy / dist * move; }
            _walkPhase += move / (_u * 62) * 2 * Math.PI;
            return dist - move < 0.5;
        }

        private Point NearestExit(Point p)
        {
            double pad = 60 * _u;
            var options = new[]
            {
                new Point(_geo.X - pad, p.Y), new Point(_geo.X + _geo.W + pad, p.Y),
                new Point(p.X, _geo.Y - pad), new Point(p.X, _geo.Y + _geo.H + pad),
            };
            Point best = options[0];
            double bestD = double.MaxValue;
            foreach (Point o in options)
            {
                double d = (o.X - p.X) * (o.X - p.X) + (o.Y - p.Y) * (o.Y - p.Y);
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        private void Show() => _win?.Present(_x, _y, new MerchantPose { Heading = _heading }, _rug);

        private static double Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
        private static double Approach(double v, double target, double step) => v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);

        private static double TurnTo(double from, double to, double rate)
        {
            double d = ((to - from) % 360 + 540) % 360 - 180;
            return from + d * Math.Min(1, rate);
        }

        public void Dispose()
        {
            try { _win?.Close(); } catch (InvalidOperationException) { }
        }
    }
}
