using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace AladdinRug
{
    /// <summary>
    /// One sweep. The merchant goes to each folder in turn, steps behind it, and pushes it across the desktop with broom strokes,
    /// the folder skidding along in front of the bristles, until it slides in under the rug (into a free spot there, or swapping
    /// with an icon already hidden there). Only folders are swept, and only icon positions ever change. All talking to Explorer
    /// happens on a worker thread so he never stutters.
    /// </summary>
    internal sealed class SweepJob : IDisposable
    {
        private enum Phase { ToFolder, Push, Tuck, Leaving, Done }

        private sealed class Folder
        {
            public string Name;
            public Point Centre;                                    // where its icon is now (screen pixels, middle of the cell)
            public Point Edge;                                      // where it goes in under the rug
            public List<(string name, int x, int y)> Targets = new List<(string, int, int)>();
        }

        private readonly DesktopIcons _icons;                      // only used on the worker thread
        private readonly SlowWork _work = new SlowWork("Rug merchant: broom");
        private readonly List<Folder> _folders = new List<Folder>();
        private readonly List<SweptItem> _swept = new List<SweptItem>();
        private readonly MerchantWindow _win;
        private readonly IntPtr _rug;
        private readonly DeskGeometry _geo;
        private readonly int _cellW, _cellH;
        private readonly Walker _walker;

        private Phase _phase = Phase.ToFolder;
        private int _current;
        private Point _stop;
        private double _t, _stroke, _nudge, _crouch, _lean;
        private int _iconIndex = -1;                              // the folder being pushed (looked up fresh on the worker)
        private volatile int _found = -1;

        public int Count => _swept.Count(s => s.Lump);

        private SweepJob(DesktopIcons icons, DeskGeometry geo, IntPtr rug, Point start)
        {
            _icons = icons; _geo = geo; _rug = rug;
            _cellW = icons.CellW; _cellH = icons.CellH;
            double u = geo.Scale * 1.7;
            _win = new MerchantWindow(u);
            _walker = new Walker(u, start.X, start.Y, 0);
        }

        // Names of the folders on the desktop (the ones in the user's own Desktop folder and the shared one).
        private static HashSet<string> DesktopFolderNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
            {
                try
                {
                    string path = Environment.GetFolderPath(dir);
                    if (Directory.Exists(path))
                        foreach (string d in Directory.GetDirectories(path))
                            if ((File.GetAttributes(d) & FileAttributes.Hidden) == 0) names.Add(Path.GetFileName(d));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return names;
        }

        /// <summary>Set up a sweep of this monitor's folders into <paramref name="body"/> (the woven part of the flat rug, screen pixels). Null, with a reason, if it can't be done.</summary>
        public static SweepJob TryCreate(DeskGeometry geo, Rect body, IntPtr rug, out string problem)
        {
            problem = null;
            DesktopIcons icons = DesktopIcons.Open();
            if (icons == null) { problem = "I can't reach the desktop icons (are they hidden?)."; return null; }
            if (icons.AutoArranged)
            {
                icons.Dispose();
                problem = "Desktop icons are set to Auto arrange, which puts them straight back. Turn it off (right-click the desktop, View) and try again.";
                return null;
            }

            List<DesktopIcon> all = icons.GetIcons();
            SweptStore.SaveBackup(all);                             // a copy of the whole layout, just in case
            int cw = icons.CellW, ch = icons.CellH;
            HashSet<string> folders = DesktopFolderNames();
            string only = Environment.GetEnvironmentVariable("RUG_SWEEP_ONLY");      // a test can narrow the sweep to folders with this name prefix
            if (!string.IsNullOrEmpty(only)) folders.RemoveWhere(n => !n.StartsWith(only, StringComparison.OrdinalIgnoreCase));
            HashSet<string> already = new HashSet<string>(SweptStore.Items.Select(i => i.Name));
            bool InBody(DesktopIcon i) => i.X >= body.Left && i.X + cw <= body.Right && i.Y >= body.Top && i.Y + ch <= body.Bottom;

            List<DesktopIcon> mine = all.Where(i => folders.Contains(i.Name) && !already.Contains(i.Name) && !InBody(i)
                                                   && i.X >= geo.X && i.X < geo.X + geo.W && i.Y >= geo.Y && i.Y < geo.Y + geo.H).ToList();
            if (mine.Count == 0) { icons.Dispose(); problem = "There are no folders left on this desktop to sweep under the rug."; return null; }

            // The cells under the rug, on the grid the icons already sit on; empty ones first.
            DesktopIcon anchor = all[0];
            double margin = 6;
            int kx0 = (int)Math.Ceiling((body.Left + margin - anchor.X) / cw), kx1 = (int)Math.Floor((body.Right - margin - cw - anchor.X) / cw);
            int ky0 = (int)Math.Ceiling((body.Top + margin - anchor.Y) / ch), ky1 = (int)Math.Floor((body.Bottom - margin - ch - anchor.Y) / ch);
            var centre = new Point(body.Left + body.Width / 2, body.Top + body.Height / 2);
            double Dist(double x, double y) => Math.Sqrt((x - centre.X) * (x - centre.X) + (y - centre.Y) * (y - centre.Y));
            var occupant = new Dictionary<(int, int), DesktopIcon>();
            foreach (DesktopIcon i in all) occupant[(i.X, i.Y)] = i;
            var free = new List<(int x, int y)>();
            var taken = new List<(int x, int y)>();
            for (int ky = ky0; ky <= ky1; ky++)
                for (int kx = kx0; kx <= kx1; kx++)
                {
                    var cell = (anchor.X + kx * cw, anchor.Y + ky * ch);
                    if (!occupant.TryGetValue(cell, out DesktopIcon who)) free.Add(cell);
                    else if (!folders.Contains(who.Name) && !already.Contains(who.Name)) taken.Add(cell);     // a hidden file: a folder can swap with it
                }
            int room = free.Count + taken.Count;
            Log.Write("sweep: body " + (int)body.Left + "," + (int)body.Top + " " + (int)body.Width + "x" + (int)body.Height + "; cell " + cw + "x" + ch +
                 "; free cells " + free.Count + ", swappable " + taken.Count + ", folders " + mine.Count);
            if (room == 0) { icons.Dispose(); problem = "The rug is too small to hide a folder under (a desktop icon needs about " + cw + "x" + ch + " pixels)."; return null; }

            // Visit the folders nearest-first, starting from the screen edge nearest the first one.
            mine = mine.OrderBy(i => Dist(i.X + cw / 2.0, i.Y + ch / 2.0)).Take(room).ToList();
            var order = new List<DesktopIcon>();
            var leftOver = mine.ToList();
            Point at = new Point(leftOver[leftOver.Count - 1].X, leftOver[leftOver.Count - 1].Y);   // start with the furthest one, work inwards
            while (leftOver.Count > 0)
            {
                DesktopIcon next = leftOver.OrderBy(i => (new Point(i.X, i.Y) - at).Length).First();
                leftOver.Remove(next);
                order.Add(next);
                at = new Point(next.X, next.Y);
            }

            Point firstAt = new Point(order[0].X + cw / 2.0, order[0].Y + ch * 0.42);
            var job = new SweepJob(icons, geo, rug, NearestEdge(geo, firstAt, 70 * geo.Scale * 1.7));
            free = free.OrderBy(c => Dist(c.x + cw / 2.0, c.y + ch / 2.0)).ToList();
            taken = taken.OrderBy(c => Dist(c.x + cw / 2.0, c.y + ch / 2.0)).ToList();
            int nextFree = 0, nextTaken = 0;
            foreach (DesktopIcon icon in order)
            {
                (int x, int y) cell;
                bool swap = nextFree >= free.Count;
                cell = swap ? taken[nextTaken++] : free[nextFree++];
                var f = new Folder { Name = icon.Name, Centre = new Point(icon.X + cw / 2.0, icon.Y + ch * 0.42) };
                // he pushes it to the rug's edge, on the side facing the folder, in line with its spot under the rug
                Point spot = new Point(cell.x + cw / 2.0, cell.y + ch * 0.42);
                f.Edge = new Point(Math.Max(body.Left + 4, Math.Min(body.Right - 4, f.Centre.X)), Math.Max(body.Top + 4, Math.Min(body.Bottom - 4, f.Centre.Y)));
                if (f.Edge == f.Centre) f.Edge = spot;
                f.Targets.Add((icon.Name, cell.x, cell.y));
                job._swept.Add(new SweptItem { Name = icon.Name, OrigX = icon.X, OrigY = icon.Y, X = cell.x, Y = cell.y, Lump = true });
                if (swap)
                {
                    DesktopIcon other = occupant[cell];
                    f.Targets.Add((other.Name, icon.X, icon.Y));               // the file hiding there takes the folder's old place
                    job._swept.Add(new SweptItem { Name = other.Name, OrigX = other.X, OrigY = other.Y, X = icon.X, Y = icon.Y, Lump = false });
                }
                job._folders.Add(f);
            }
            job._walker.Heading = Walker.AngleTo(job._walker.X, job._walker.Y, firstAt.X, firstAt.Y);
            job.BeginFolder();
            return job;
        }

        private static Point NearestEdge(DeskGeometry geo, Point p, double pad)
        {
            var options = new[] { new Point(geo.X - pad, p.Y), new Point(geo.X + geo.W + pad, p.Y), new Point(p.X, geo.Y - pad), new Point(p.X, geo.Y + geo.H + pad) };
            return options.OrderBy(o => (o.X - p.X) * (o.X - p.X) + (o.Y - p.Y) * (o.Y - p.Y)).First();
        }

        private static double Approach(double v, double target, double step) => v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);

        private Folder Current => _folders[_current];

        // Where the broom's bristles are: a little way in front of him.
        private Point BroomHead()
        {
            double rad = _walker.Heading * Math.PI / 180, ahead = 50 * _walker.Unit;
            return new Point(_walker.X + Math.Cos(rad) * ahead, _walker.Y + Math.Sin(rad) * ahead);
        }

        private void BeginFolder()
        {
            Folder f = Current;
            // stand behind the folder, on the far side from the rug, with the broom just touching it
            Vector toRug = f.Edge - f.Centre;
            if (toRug.Length < 1) toRug = new Vector(1, 0);
            toRug.Normalize();
            _stop = f.Centre - toRug * (50 * _walker.Unit);
            _phase = Phase.ToFolder;
            _t = 0;
            _found = -1;
            _iconIndex = -1;
            string name = f.Name;
            _work.Post(() => { DesktopIcon? i = _icons.Find(name); _found = i.HasValue ? i.Value.Index : -2; });
        }

        public bool Update(double dt)
        {
            _t += dt;
            double swing;
            switch (_phase)
            {
                case Phase.ToFolder:
                {
                    _crouch = Approach(_crouch, 0, dt * 4); _lean = Approach(_lean, 0.35, dt * 3);
                    _stroke += dt * (2 + 4 * _walker.Walk);
                    swing = Math.Sin(_stroke) * 0.25;                          // carrying the broom, bristles just off the ground
                    Folder f = Current;
                    if (_walker.Go(_stop, dt) && _walker.Face(Walker.AngleTo(_walker.X, _walker.Y, f.Edge.X, f.Edge.Y), dt) && _found != -1)
                    {
                        _iconIndex = _found;
                        _phase = Phase.Push;
                        _t = 0;
                        _nudge = 0;
                    }
                    break;
                }

                case Phase.Push:
                {
                    // short brisk strokes, walking slowly behind the broom; the folder skids along in front of it
                    Folder f = Current;
                    _crouch = Approach(_crouch, 0.25, dt * 3); _lean = Approach(_lean, 0.9, dt * 3);
                    _stroke += dt * 7.5;
                    swing = Math.Sin(_stroke);
                    double pushSpeed = 85 * _walker.Unit * (0.55 + 0.45 * Math.Abs(Math.Cos(_stroke)));
                    Vector toRug = f.Edge - f.Centre;
                    toRug.Normalize();
                    Point behind = f.Edge - toRug * (50 * _walker.Unit);
                    bool arrived = _walker.Go(behind.X, behind.Y, dt, pushSpeed);

                    _nudge -= dt;
                    if (_nudge <= 0 && _iconIndex >= 0)
                    {
                        _nudge = 0.14;
                        Point head = BroomHead();
                        int index = _iconIndex;
                        int x = (int)Math.Round(head.X - _cellW / 2.0), y = (int)Math.Round(head.Y - _cellH * 0.42);
                        _work.Post(() => _icons.Move(index, x, y));
                    }
                    if (arrived || _t > 12) { _phase = Phase.Tuck; _t = 0; Folder done = f; _work.Post(() => _icons.Arrange(done.Targets)); }
                    break;
                }

                case Phase.Tuck:
                    // one last shove, and it's under
                    _crouch = Approach(_crouch, 0.35, dt * 4); _lean = Approach(_lean, 1, dt * 4);
                    _stroke += dt * 9;
                    swing = Math.Sin(_stroke) * (1 - Math.Min(1, _t / 0.5));
                    if (_t > 0.55)
                    {
                        _current++;
                        if (_current < _folders.Count) BeginFolder();
                        else
                        {
                            _phase = Phase.Leaving;
                            _stop = NearestEdge(_geo, new Point(_walker.X, _walker.Y), 70 * _walker.Unit);
                            _work.Post(Finish);
                        }
                    }
                    break;

                case Phase.Leaving:
                    _crouch = Approach(_crouch, 0, dt * 4); _lean = Approach(_lean, 0.1, dt * 3);
                    _stroke += dt * 5;
                    swing = Math.Sin(_stroke) * 0.15;
                    if (_walker.Go(_stop, dt) && !_work.Busy) { _phase = Phase.Done; _win.Vanish(); return false; }
                    break;

                default:
                    return false;
            }

            _win.Present(_walker.X, _walker.Y, new MerchantPose
            {
                Heading = _walker.Heading, Phase = _walker.Phase, Walk = _walker.Walk, Lean = _lean, Crouch = _crouch, Reach = 0.9,
                Broom = 1, BroomSwing = swing,
            }, _rug);
            return true;
        }

        // On the worker: note where everything really ended up (the desktop may have nudged one into a neighbouring cell).
        private void Finish()
        {
            Dictionary<string, DesktopIcon> now = _icons.GetIcons().GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.First());
            foreach (SweptItem s in _swept)
                if (now.TryGetValue(s.Name, out DesktopIcon icon)) { s.X = icon.X; s.Y = icon.Y; }
            SweptStore.Add(_swept, _cellW, _cellH);
        }

        public void Dispose()
        {
            try { _win.Close(); } catch (InvalidOperationException) { }
            _work.Dispose();
            _icons.Dispose();
        }

        /// <summary>Stop now: whatever has been tucked under stays recorded; the folder he was pushing goes back where it was.</summary>
        public void Abort()
        {
            if (_phase == Phase.Done) return;
            var done = new List<SweptItem>();
            for (int n = 0; n < _current && n < _folders.Count; n++)
                done.AddRange(_swept.Where(s => _folders[n].Targets.Any(t => t.name == s.Name)));
            var back = _swept.Except(done).Select(s => (s.Name, s.OrigX, s.OrigY)).ToList();
            _work.Post(() =>
            {
                _icons.Arrange(back);
                if (done.Count > 0)
                {
                    Dictionary<string, DesktopIcon> now = _icons.GetIcons().GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.First());
                    foreach (SweptItem s in done) if (now.TryGetValue(s.Name, out DesktopIcon icon)) { s.X = icon.X; s.Y = icon.Y; }
                    SweptStore.Add(done, _cellW, _cellH);
                }
            });
            _work.Drain(8000);
            _phase = Phase.Done;
            _win.Vanish();
        }
    }
}
