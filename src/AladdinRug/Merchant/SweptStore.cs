using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AladdinRug
{
    /// <summary>An icon that has been swept under the rug: where it was, and where it is now (screen pixels, its cell's top-left).</summary>
    internal sealed class SweptItem
    {
        public string Name;
        public int OrigX, OrigY, X, Y;
        public bool Lump = true;      // false for an icon that only changed places with one that went under (it is in plain sight)
    }

    /// <summary>
    /// Remembers every icon that is under the rug, so each can be put back exactly where it was (even after a restart).
    /// Only icon positions are ever changed: no file or folder is moved, renamed or deleted.
    /// </summary>
    internal static class SweptStore
    {
        private static readonly object Gate = new object();
        private static List<SweptItem> _items;
        private static int _cellW = 95, _cellH = 107;

        public static event Action Changed;

        private static string FilePath => Path.Combine(AppFolders.Roaming, "swept.txt");

        public static int CellW { get { lock (Gate) { Load(); return _cellW; } } }
        public static int CellH { get { lock (Gate) { Load(); return _cellH; } } }

        public static List<SweptItem> Items
        {
            get { lock (Gate) { Load(); return _items.ToList(); } }
        }

        public static int Count
        {
            get { lock (Gate) { Load(); return _items.Count; } }
        }

        private static void Load()
        {
            if (_items != null) return;
            _items = new List<SweptItem>();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    if (line.StartsWith("cell="))
                    {
                        string[] c = line.Substring(5).Split(',');
                        if (c.Length == 2 && int.TryParse(c[0], out int cw) && int.TryParse(c[1], out int ch)) { _cellW = cw; _cellH = ch; }
                        continue;
                    }
                    string[] f = line.Split('|');
                    if (f.Length >= 5 && int.TryParse(f[1], out int ox) && int.TryParse(f[2], out int oy) && int.TryParse(f[3], out int x) && int.TryParse(f[4], out int y))
                        _items.Add(new SweptItem { Name = f[0], OrigX = ox, OrigY = oy, X = x, Y = y, Lump = f.Length < 6 || f[5] != "0" });
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string> { "cell=" + _cellW + "," + _cellH };
                lines.AddRange(_items.Select(i => i.Name + "|" + i.OrigX + "|" + i.OrigY + "|" + i.X + "|" + i.Y + "|" + (i.Lump ? "1" : "0")));
                string temp = FilePath + ".tmp";
                File.WriteAllLines(temp, lines);
                File.Move(temp, FilePath, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>A copy of where every icon was before a sweep, in case anything ever goes wrong: <c>layout-backup.txt</c> (name|x|y).</summary>
        public static void SaveBackup(IEnumerable<DesktopIcon> all)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(Path.Combine(Path.GetDirectoryName(FilePath), "layout-backup.txt"), all.Select(i => i.Name + "|" + i.X + "|" + i.Y));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static void Add(IEnumerable<SweptItem> items, int cellW, int cellH)
        {
            lock (Gate)
            {
                Load();
                _cellW = cellW; _cellH = cellH;
                foreach (SweptItem i in items)
                {
                    _items.RemoveAll(o => o.Name == i.Name);       // swept twice: keep the first original place
                    _items.Add(i);
                }
                Save();
            }
            Changed?.Invoke();
        }

        /// <summary>Put the swept icons that <paramref name="which"/> picks (all of them when null) back where they were. Returns how many went back.</summary>
        public static int PutBack(Func<SweptItem, bool> which, out string problem)
        {
            problem = null;
            int back = 0;
            lock (Gate)
            {
                Load();
                if (_items.Count == 0) return 0;
                using (DesktopIcons icons = DesktopIcons.Open())
                {
                    if (icons == null) { problem = "Can't reach the desktop icons just now."; return 0; }
                    List<SweptItem> chosen = _items.Where(it => which == null || which(it)).ToList();
                    back = icons.Arrange(chosen.Select(it => (it.Name, it.OrigX, it.OrigY)));
                    foreach (SweptItem it in chosen) _items.Remove(it);     // (an icon since deleted or renamed is simply forgotten)
                }
                Save();
            }
            Changed?.Invoke();
            return back;
        }
    }
}
