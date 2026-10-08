using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace AladdinRug
{
    /// <summary>
    /// The merchant tidying the desktop. For each armful (one to five files of one kind, in random order) he walks to each file in
    /// turn, crouches and picks it up (the file really leaves the desktop then), carries the armful to its folder (made the first
    /// time it's needed, wherever Explorer puts it), crouches and puts them in. Slow work - moving files, asking Explorer where
    /// icons are - happens on a worker thread so he never stalls.
    /// </summary>
    internal sealed class TidyJob : IDisposable
    {
        private enum Phase { Arriving, ToFile, Pick, ToFolder, Drop, Leaving, Done }

        private const double PickSeconds = 0.62, DropSeconds = 0.55;

        private readonly DeskGeometry _geo;
        private readonly IntPtr _rug;
        private readonly Rect _rugArea;                          // where the rug lies (screen pixels): he heads there while a new folder appears
        private readonly MerchantWindow _win;
        private readonly SlowWork _work = new SlowWork("Rug merchant: files");
        private readonly DesktopIcons _icons;                    // used only on the worker thread; may be null
        private readonly Organizer.Plan _plan;
        private readonly Dictionary<string, Point> _fileAt;
        private readonly ConcurrentDictionary<string, Point> _folderAt = new ConcurrentDictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        private readonly int _cellW, _cellH;
        private readonly Walker _walker;

        private Phase _phase = Phase.Arriving;
        private int _trip = -1, _fileInTrip;
        private List<string> _route = new List<string>();      // the files of this armful, in the order he'll pick them up
        private Point _stop;                                     // where he stands for the current pick or drop
        private Point _faceAt;                                   // what he looks at while crouching
        private double _t, _crouch, _lean, _reach, _lookup;
        private int _carrying, _moved;
        private double _hue;
        private volatile bool _lookingUp;

        public int FilesMoved => Volatile.Read(ref _moved);
        public int FolderCount => _plan.FolderCount;
        public event Action Finished;

        private TidyJob(DeskGeometry geo, IntPtr rug, Rect rugArea, DesktopIcons icons, Organizer.Plan plan, Dictionary<string, Point> fileAt, Point start)
        {
            _geo = geo; _rug = rug; _rugArea = rugArea; _icons = icons; _plan = plan; _fileAt = fileAt;
            double u = geo.Scale * 1.7;
            _win = new MerchantWindow(u);
            _cellW = icons?.CellW ?? 95; _cellH = icons?.CellH ?? 107;
            _walker = new Walker(u, start.X, start.Y, 0);
            // a lot to do: he moves at a brisk trot rather than a stroll
            _walker.Cruise = 150 * u * Math.Max(1, Math.Min(1.6, 1 + (plan.Trips.Count - 15) / 40.0));
        }

        /// <summary>The files this job would tidy: the loose ones on the desktop (a test can narrow them with RUG_ORGANIZE_ONLY=prefix).</summary>
        public static List<string> FilesToTidy()
        {
            string only = Environment.GetEnvironmentVariable("RUG_ORGANIZE_ONLY");
            List<string> files = Organizer.LooseFiles();
            return string.IsNullOrEmpty(only) ? files : files.Where(f => Path.GetFileName(f).StartsWith(only, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public static TidyJob TryCreate(DeskGeometry geo, IntPtr rug, Rect rugArea, out string problem)
        {
            problem = null;
            List<string> files = FilesToTidy();
            if (files.Count == 0) { problem = "There are no loose files on the desktop to tidy."; return null; }

            DesktopIcons icons = DesktopIcons.Open();
            var fileAt = new Dictionary<string, Point>();
            if (icons != null)
            {
                var byName = new Dictionary<string, DesktopIcon>(StringComparer.OrdinalIgnoreCase);
                foreach (DesktopIcon i in icons.GetIcons()) if (!byName.ContainsKey(i.Name)) byName[i.Name] = i;
                foreach (string f in files)
                    if (byName.TryGetValue(Path.GetFileName(f), out DesktopIcon icon) || byName.TryGetValue(Path.GetFileNameWithoutExtension(f), out icon))
                        fileAt[f] = new Point(icon.X + icons.CellW / 2.0, icon.Y + icons.CellH * 0.42);
            }

            Organizer.Plan plan = Organizer.MakePlan(files, f => fileAt.TryGetValue(f, out Point p) ? p : (Point?)null, new Random());
            Point first = plan.Trips.SelectMany(t => t.Files).Where(fileAt.ContainsKey).Select(f => fileAt[f]).DefaultIfEmpty(new Point(geo.X + geo.W / 2.0, geo.Y + geo.H / 2.0)).First();
            Point entry = NearestEdge(geo, first, 70 * geo.Scale * 1.7);
            var job = new TidyJob(geo, rug, rugArea, icons, plan, fileAt, entry);
            job._walker.Heading = Walker.AngleTo(entry.X, entry.Y, first.X, first.Y);
            return job;
        }

        private static Point NearestEdge(DeskGeometry geo, Point p, double pad)
        {
            var options = new[] { new Point(geo.X - pad, p.Y), new Point(geo.X + geo.W + pad, p.Y), new Point(p.X, geo.Y - pad), new Point(p.X, geo.Y + geo.H + pad) };
            return options.OrderBy(o => (o.X - p.X) * (o.X - p.X) + (o.Y - p.Y) * (o.Y - p.Y)).First();
        }

        private static double Approach(double v, double target, double step) => v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);

        private Point Where(string file) => _fileAt.TryGetValue(file, out Point p) ? p : new Point(_walker.X, _walker.Y);

        // Stand just short of a spot, facing it, so his hands reach it.
        private Point StandBefore(Point spot)
        {
            double dx = spot.X - _walker.X, dy = spot.Y - _walker.Y, d = Math.Sqrt(dx * dx + dy * dy);
            double reach = 30 * _walker.Unit;
            if (d < reach + 1) return new Point(_walker.X, _walker.Y);
            return new Point(spot.X - dx / d * reach, spot.Y - dy / d * reach);
        }

        private void StartTrip()
        {
            _trip++;
            if (_trip >= _plan.Trips.Count)
            {
                _phase = Phase.Leaving;
                _stop = NearestEdge(_geo, new Point(_walker.X, _walker.Y), 70 * _walker.Unit);
                return;
            }
            // pick the armful up nearest-first, starting from where he stands
            var left = _plan.Trips[_trip].Files.ToList();
            _route = new List<string>();
            var at = new Point(_walker.X, _walker.Y);
            while (left.Count > 0)
            {
                string next = left.OrderBy(f => (Where(f) - at).Length).First();
                left.Remove(next);
                _route.Add(next);
                at = Where(next);
            }
            _fileInTrip = 0;
            _hue = (_plan.Trips[_trip].Folder.GetHashCode() & 0x7FFFFFFF) % 360;
            GoToFile();
        }

        private void GoToFile()
        {
            _faceAt = Where(_route[_fileInTrip]);
            _stop = StandBefore(_faceAt);
            _phase = Phase.ToFile;
        }

        // Ask (on the worker) where a folder's icon is; it appears a moment after the folder is made.
        private void LookUpFolder(string folder)
        {
            if (_lookingUp || _icons == null || _folderAt.ContainsKey(folder)) return;
            _lookingUp = true;
            _work.Post(() =>
            {
                try
                {
                    DesktopIcon? icon = _icons.Find(folder);
                    if (icon.HasValue) _folderAt[folder] = new Point(icon.Value.X + _cellW / 2.0, icon.Value.Y + _cellH * 0.42);
                }
                finally { _lookingUp = false; }
            });
        }

        public bool Update(double dt)
        {
            _t += dt;
            switch (_phase)
            {
                case Phase.Arriving:
                    _crouch = Approach(_crouch, 0, dt * 3); _lean = Approach(_lean, 0.1, dt * 3); _reach = Approach(_reach, 0.15, dt * 3);
                    StartTrip();
                    break;

                case Phase.ToFile:
                    _crouch = Approach(_crouch, 0, dt * 4); _lean = Approach(_lean, _carrying > 0 ? 0.3 : 0.1, dt * 3); _reach = Approach(_reach, _carrying > 0 ? 0.8 : 0.15, dt * 4);
                    if (_walker.Go(_stop, dt) && _walker.Face(Walker.AngleTo(_walker.X, _walker.Y, _faceAt.X, _faceAt.Y), dt)) { _phase = Phase.Pick; _t = 0; }
                    break;

                case Phase.Pick:
                {
                    // bend down, take it, straighten up
                    double k = _t / PickSeconds;
                    _crouch = Math.Sin(Math.Min(1, k) * Math.PI) * 0.8;
                    _lean = 0.35 + 0.55 * Math.Sin(Math.Min(1, k) * Math.PI);
                    _reach = 0.8 + 0.2 * Math.Sin(Math.Min(1, k) * Math.PI);
                    if (k >= 0.5 && _carrying <= _fileInTrip)
                    {
                        string file = _route[_fileInTrip], folder = _plan.Trips[_trip].Folder;
                        _carrying = _fileInTrip + 1;
                        _work.Post(() => { if (File.Exists(file) && Organizer.MoveInto(file, folder) != null) Interlocked.Increment(ref _moved); });
                    }
                    if (k >= 1)
                    {
                        _fileInTrip++;
                        if (_fileInTrip < _route.Count) GoToFile();
                        else
                        {
                            string folder = _plan.Trips[_trip].Folder;
                            LookUpFolder(folder);
                            _faceAt = _folderAt.TryGetValue(folder, out Point f) ? f : new Point(_rugArea.Left + _rugArea.Width / 2, _rugArea.Top + _rugArea.Height / 2);
                            _stop = StandBefore(_faceAt);
                            _phase = Phase.ToFolder;
                            _t = 0;
                            _lookup = 0.25;
                        }
                    }
                    break;
                }

                case Phase.ToFolder:
                {
                    string folder = _plan.Trips[_trip].Folder;
                    _crouch = Approach(_crouch, 0, dt * 4); _lean = Approach(_lean, 0.3, dt * 3); _reach = Approach(_reach, 0.8, dt * 4);
                    bool known = _folderAt.TryGetValue(folder, out Point f);
                    if (!known)
                    {
                        _lookup -= dt;
                        if (_lookup <= 0) { _lookup = 0.3; LookUpFolder(folder); }
                    }
                    else if ((f - _faceAt).Length > 1) { _faceAt = f; _stop = StandBefore(f); }     // the folder has appeared: go there instead
                    bool there = _walker.Go(_stop, dt) && _walker.Face(Walker.AngleTo(_walker.X, _walker.Y, _faceAt.X, _faceAt.Y), dt);
                    if (there && (known || _icons == null || _t > 4)) { _phase = Phase.Drop; _t = 0; }
                    break;
                }

                case Phase.Drop:
                {
                    double k = _t / DropSeconds;
                    _crouch = Math.Sin(Math.Min(1, k) * Math.PI) * 0.7;
                    _lean = 0.3 + 0.6 * Math.Sin(Math.Min(1, k) * Math.PI);
                    if (k >= 0.5) _carrying = 0;                          // in they go
                    if (k >= 1) StartTrip();
                    break;
                }

                case Phase.Leaving:
                    _crouch = Approach(_crouch, 0, dt * 4); _lean = Approach(_lean, 0.05, dt * 3); _reach = Approach(_reach, 0, dt * 3);
                    if (_walker.Go(_stop, dt) && !_work.Busy) { _phase = Phase.Done; _win.Vanish(); Finished?.Invoke(); return false; }
                    break;

                case Phase.Done:
                    return false;
            }

            _win.Present(_walker.X, _walker.Y, new MerchantPose
            {
                Heading = _walker.Heading, Phase = _walker.Phase, Walk = _walker.Walk, Lean = _lean, Crouch = _crouch, Reach = _reach,
                Carry = _carrying, CarryHue = _hue,
            }, _rug);
            return true;
        }

        /// <summary>Do all the rest at once (the user asked him to hurry).</summary>
        public void FinishNow()
        {
            if (_phase == Phase.Done) return;
            for (int t = Math.Max(0, _trip); t < _plan.Trips.Count; t++)
                foreach (string f in _plan.Trips[t].Files)
                {
                    string file = f, folder = _plan.Trips[t].Folder;
                    _work.Post(() => { if (File.Exists(file) && Organizer.MoveInto(file, folder) != null) Interlocked.Increment(ref _moved); });
                }
            _work.Drain(15000);
            _phase = Phase.Done;
            _win.Vanish();
            Finished?.Invoke();
        }

        public void Dispose()
        {
            try { _win.Close(); } catch (InvalidOperationException) { }
            _work.Dispose();
            _icons?.Dispose();
        }
    }
}
