using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace AladdinRug
{
    /// <summary>
    /// Sorting the desktop's files into folders by extension, for real. Folders are created on the desktop and files are MOVED into
    /// them. Nothing is ever deleted or overwritten (a clash gets a new name), and every move is written to a list so the whole
    /// thing can be undone.
    /// </summary>
    internal static class Organizer
    {
        public sealed class Trip
        {
            public string Folder;                          // the folder's name
            public List<string> Files = new List<string>();
        }

        public sealed class Plan
        {
            public List<Trip> Trips = new List<Trip>();
            public Dictionary<string, int> PerFolder = new Dictionary<string, int>();
            public int FileCount => PerFolder.Values.Sum();
            public int FolderCount => PerFolder.Count;
        }

        /// <summary>The desktop folder to tidy (settable so tests can use a scratch folder).</summary>
        public static string DesktopPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        /// <summary>Where the undo list is kept (settable so tests don't touch the real one).</summary>
        public static string ManifestOverride { get; set; }

        private static string ManifestPath => ManifestOverride ?? Path.Combine(AppFolders.Roaming, "organized.txt");
        private static readonly object Gate = new object();

        // ------------------------------------------------------------------ planning

        // "pdf" -> "PDF files", shortcuts -> "Shortcuts", and so on.
        public static string FolderNameFor(string extension)
        {
            string e = extension.TrimStart('.').ToLowerInvariant();
            if (e.Length == 0) return "No extension";
            if (e == "lnk") return "Shortcuts";
            if (e == "url") return "Web links";
            return e.ToUpperInvariant() + " files";
        }

        /// <summary>The files lying loose on the desktop (not folders, not hidden or system files, not this program).</summary>
        public static List<string> LooseFiles()
        {
            var files = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(DesktopPath))
                {
                    FileAttributes a = File.GetAttributes(f);
                    if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (string.Equals(f, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(f);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return files;
        }

        /// <summary>
        /// Group the files by extension (an extension with a single file goes into "Other files"), then split each group into
        /// armfuls of one to five, picking neighbours together when their desktop positions are known, and shuffle the lot.
        /// </summary>
        public static Plan MakePlan(IEnumerable<string> files, Func<string, Point?> where, Random rnd)
        {
            var plan = new Plan();
            var groups = files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).ToList();
            var byFolder = new Dictionary<string, List<string>>();
            foreach (var g in groups)
            {
                string folder = g.Count() < 2 ? "Other files" : FolderNameFor(g.Key);
                if (!byFolder.TryGetValue(folder, out List<string> list)) byFolder[folder] = list = new List<string>();
                list.AddRange(g);
            }

            foreach (var kv in byFolder)
            {
                plan.PerFolder[kv.Key] = kv.Value.Count;
                var left = kv.Value.OrderBy(_ => rnd.Next()).ToList();
                while (left.Count > 0)
                {
                    string seed = left[0];
                    left.RemoveAt(0);
                    var trip = new Trip { Folder = kv.Key };
                    trip.Files.Add(seed);
                    int size = 1 + rnd.Next(5);                               // one to five at a time
                    Point? seedAt = where?.Invoke(seed);
                    while (trip.Files.Count < size && left.Count > 0)
                    {
                        string next = seedAt.HasValue
                            ? left.OrderBy(f => { Point? p = where(f); return p.HasValue ? (p.Value - seedAt.Value).Length : 1e9; }).First()
                            : left[0];
                        left.Remove(next);
                        trip.Files.Add(next);
                    }
                    plan.Trips.Add(trip);
                }
            }
            plan.Trips = plan.Trips.OrderBy(_ => rnd.Next()).ToList();        // random order
            return plan;
        }

        // ------------------------------------------------------------------ doing it

        public static string FolderPath(string folderName) => Path.Combine(DesktopPath, folderName);

        /// <summary>Make the folder if it isn't there yet (an existing one is used as it is). True if it was created now.</summary>
        public static bool EnsureFolder(string folderName)
        {
            string path = FolderPath(folderName);
            if (Directory.Exists(path)) return false;
            Directory.CreateDirectory(path);
            Record("F", path, "");
            return true;
        }

        /// <summary>Move one file into the folder. A clash with a file already there gets a new name. Returns where it went, or null.</summary>
        public static string MoveInto(string file, string folderName)
        {
            try
            {
                EnsureFolder(folderName);
                string dir = FolderPath(folderName), name = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
                string target = Path.Combine(dir, name + ext);
                for (int n = 2; File.Exists(target) || Directory.Exists(target); n++) target = Path.Combine(dir, name + " (" + n + ")" + ext);
                File.Move(file, target);
                Record("M", file, target);
                return target;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void Record(string kind, string a, string b)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath));
                    File.AppendAllText(ManifestPath, kind + "|" + a + "|" + b + Environment.NewLine);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // ------------------------------------------------------------------ undoing it

        /// <summary>How many files are sitting in folders because of a tidy-up that hasn't been undone.</summary>
        public static int Tidied
        {
            get
            {
                lock (Gate)
                {
                    try { return File.Exists(ManifestPath) ? File.ReadAllLines(ManifestPath).Count(l => l.StartsWith("M|")) : 0; }
                    catch (IOException) { return 0; }
                }
            }
        }

        /// <summary>Put every moved file back where it came from, and remove the folders that were made (only if they are empty).</summary>
        public static int Undo(out string problem)
        {
            problem = null;
            int back = 0, failed = 0;
            lock (Gate)
            {
                if (!File.Exists(ManifestPath)) return 0;
                string[] lines;
                try { lines = File.ReadAllLines(ManifestPath); }
                catch (IOException ex) { problem = ex.Message; return 0; }

                foreach (string line in lines.Reverse())
                {
                    string[] f = line.Split('|');
                    if (f.Length < 3 || f[0] != "M") continue;
                    try
                    {
                        if (!File.Exists(f[2])) continue;                    // gone from there already: nothing to put back
                        string target = f[1];
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string name = Path.GetFileNameWithoutExtension(target), ext = Path.GetExtension(target);
                        for (int n = 2; File.Exists(target); n++) target = Path.Combine(Path.GetDirectoryName(f[1]), name + " (back " + n + ")" + ext);
                        File.Move(f[2], target);
                        back++;
                    }
                    catch (IOException) { failed++; }
                    catch (UnauthorizedAccessException) { failed++; }
                }

                foreach (string line in lines.Reverse())
                {
                    string[] f = line.Split('|');
                    if (f.Length < 2 || f[0] != "F") continue;
                    // only a folder this tidy-up made, and only if it is empty now. (Synced desktops mark folders read-only, which blocks deleting.)
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        try
                        {
                            if (!Directory.Exists(f[1]) || Directory.EnumerateFileSystemEntries(f[1]).Any()) break;
                            var info = new DirectoryInfo(f[1]);
                            if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes &= ~FileAttributes.ReadOnly;
                            Directory.Delete(f[1]);
                            break;
                        }
                        catch (IOException) { System.Threading.Thread.Sleep(150); }
                        catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(150); }
                    }
                }

                if (failed == 0) { try { File.Delete(ManifestPath); } catch (IOException) { } }
                else problem = failed + " files couldn't be put back (they are still in their folders).";
            }
            return back;
        }

    }
}
