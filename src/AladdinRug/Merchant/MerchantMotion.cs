using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows;

namespace AladdinRug
{
    /// <summary>
    /// How the merchant moves: he turns towards where he is going (on the spot if it's behind him), speeds up and slows down
    /// instead of starting and stopping dead, and his steps match the ground he covers, so his feet don't slide.
    /// Positions are screen pixels; <see cref="Unit"/> is pixels per drawing unit.
    /// </summary>
    internal sealed class Walker
    {
        public double X, Y, Heading, Speed, Phase;
        public readonly double Unit;
        public double Cruise;                       // walking speed, pixels per second
        public double Accel, Brake;                 // how fast he speeds up and slows down, pixels per second squared

        private const double TurnRate = 420;        // degrees per second
        private const double StrideUnits = 62;      // ground covered by one full walking cycle

        public Walker(double unit, double x, double y, double heading)
        {
            Unit = unit; X = x; Y = y; Heading = heading;
            Cruise = 150 * unit;
            Accel = 500 * unit;
            Brake = 700 * unit;
        }

        /// <summary>0 standing, 1 walking at an ordinary pace (more when hurrying).</summary>
        public double Walk => Math.Min(1.25, Speed / (150 * Unit));

        public static double AngleTo(double fromX, double fromY, double toX, double toY) => Math.Atan2(toY - fromY, toX - fromX) * 180 / Math.PI;

        public static double Diff(double from, double to) => ((to - from) % 360 + 540) % 360 - 180;

        /// <summary>Turn on the spot towards a heading. True once facing it.</summary>
        public bool Face(double heading, double dt)
        {
            Speed = Math.Max(0, Speed - 900 * Unit * dt);
            double d = Diff(Heading, heading);
            Heading += Math.Max(-TurnRate * dt, Math.Min(TurnRate * dt, d));
            return Math.Abs(d) < 4;
        }

        /// <summary>Walk to a point and stop there. True on arrival.</summary>
        public bool Go(Point target, double dt) => Go(target.X, target.Y, dt, Cruise);

        public bool Go(double tx, double ty, double dt, double cruise)
        {
            double dx = tx - X, dy = ty - Y, dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 0.75 && Speed < 20 * Unit) { Speed = 0; return true; }

            double want = Math.Atan2(dy, dx) * 180 / Math.PI;
            double d = Diff(Heading, want);
            Heading += Math.Max(-TurnRate * dt, Math.Min(TurnRate * dt, d));

            // walk the way he faces: barely move while turning round, and ease off as he gets close
            double facing = Math.Max(0, Math.Cos(Diff(Heading, want) * Math.PI / 180));
            double braking = Math.Sqrt(2 * Brake * dist);
            double target = Math.Min(cruise, braking) * facing * facing;
            double accel = Accel;
            Speed = Speed < target ? Math.Min(target, Speed + accel * dt) : Math.Max(target, Speed - accel * 2 * dt);

            double step = Math.Min(dist, Speed * dt);
            double rad = Heading * Math.PI / 180;
            X += Math.Cos(rad) * step;
            Y += Math.Sin(rad) * step;
            Phase += step / (Unit * StrideUnits) * 2 * Math.PI;
            return false;
        }
    }

    /// <summary>
    /// A worker thread for anything slow (moving files, asking Explorer about icons), so the merchant never stutters while he
    /// waits for it. Jobs run one at a time, in order.
    /// </summary>
    internal sealed class SlowWork : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private int _pending;

        public SlowWork(string name)
        {
            _thread = new Thread(() =>
            {
                foreach (Action a in _queue.GetConsumingEnumerable())
                {
                    try { a(); }
                    catch (Exception) { }               // a failed move or icon query just doesn't happen; the job carries on
                    finally { Interlocked.Decrement(ref _pending); }
                }
            }) { IsBackground = true, Name = name };
            _thread.Start();
        }

        /// <summary>True while anything is still waiting or running.</summary>
        public bool Busy => Volatile.Read(ref _pending) > 0;

        public void Post(Action work)
        {
            Interlocked.Increment(ref _pending);
            _queue.Add(work);
        }

        /// <summary>Wait (a little) for everything posted so far.</summary>
        public void Drain(int timeoutMs)
        {
            var start = Environment.TickCount64;
            while (Busy && Environment.TickCount64 - start < timeoutMs) Thread.Sleep(10);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(3000);
        }
    }
}
