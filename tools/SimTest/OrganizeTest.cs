using System;
using System.IO;
using System.Linq;

namespace AladdinRug
{
    /// <summary>
    /// Checks the file sorter on made-up files in a scratch folder (the real desktop and the real undo list are never touched):
    /// what gets sorted where, that nothing is overwritten, that existing folders are left alone, and that undo restores everything.
    /// </summary>
    internal static class OrganizeTest
    {
        private static int _fails;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
            if (!ok) _fails++;
        }

        public static int Run(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            string desk = Path.Combine(dir, "desk");
            Directory.CreateDirectory(desk);
            Organizer.DesktopPath = desk;
            Organizer.ManifestOverride = Path.Combine(dir, "organized.txt");

            string[] names = { "a.pdf", "b.pdf", "c.PDF", "d.txt", "e.txt", "readme", "x.docx", "notes.md", "t.lnk", "u.lnk" };
            foreach (string n in names) File.WriteAllText(Path.Combine(desk, n), "data of " + n);
            Directory.CreateDirectory(Path.Combine(desk, "My folder"));                       // an existing folder: left alone
            File.WriteAllText(Path.Combine(desk, "My folder", "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(desk, "hidden.dat"), "h");                         // a hidden file: left alone
            File.SetAttributes(Path.Combine(desk, "hidden.dat"), FileAttributes.Hidden);

            var loose = Organizer.LooseFiles();
            Check(loose.Count == names.Length, "loose files found: " + loose.Count + " (hidden file and folder ignored)");

            var plan = Organizer.MakePlan(loose, null, new Random(1));
            Check(plan.FileCount == names.Length, "plan covers every file (" + plan.FileCount + ")");
            Check(plan.PerFolder.TryGetValue("PDF files", out int pdfs) && pdfs == 3, "PDFs together whatever their case: " + string.Join(", ", plan.PerFolder.Select(kv => kv.Key + "=" + kv.Value)));
            Check(plan.PerFolder.ContainsKey("Shortcuts") && plan.PerFolder.ContainsKey("TXT files") && plan.PerFolder.ContainsKey("Other files"), "shortcuts, txt and 'other' folders planned");
            Check(plan.Trips.All(t => t.Files.Count >= 1 && t.Files.Count <= 5), "every armful is one to five files");

            // a name clash inside a folder must not overwrite
            Directory.CreateDirectory(Path.Combine(desk, "TXT files"));
            File.WriteAllText(Path.Combine(desk, "TXT files", "d.txt"), "ALREADY HERE");
            foreach (var trip in plan.Trips)
                foreach (string f in trip.Files) Check(Organizer.MoveInto(f, trip.Folder) != null, "moved " + Path.GetFileName(f));
            Check(File.ReadAllText(Path.Combine(desk, "TXT files", "d.txt")) == "ALREADY HERE", "the file already there was not overwritten");
            Check(File.Exists(Path.Combine(desk, "TXT files", "d (2).txt")), "the clashing file got a new name");
            Check(File.ReadAllText(Path.Combine(desk, "My folder", "keep.txt")) == "keep", "existing folder untouched");
            Check(Organizer.LooseFiles().Count == 0, "nothing loose is left on the desktop");
            Check(Organizer.Tidied == names.Length, "undo list has every move (" + Organizer.Tidied + ")");

            int back = Organizer.Undo(out string problem);
            Check(problem == null && back == names.Length, "undo put back " + back);
            Check(names.All(n => File.Exists(Path.Combine(desk, n)) && File.ReadAllText(Path.Combine(desk, n)) == "data of " + n), "every file is back with its own contents");
            Check(!Directory.Exists(Path.Combine(desk, "PDF files")) && !Directory.Exists(Path.Combine(desk, "Shortcuts")), "folders that were made are gone again");
            Check(Directory.Exists(Path.Combine(desk, "TXT files")), "a folder that was already there stays (it still holds its own file)");

            Console.WriteLine(_fails == 0 ? "ALL GOOD" : _fails + " FAILED");
            return _fails;
        }
    }
}
