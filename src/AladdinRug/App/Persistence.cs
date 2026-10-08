using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace AladdinRug
{
    /// <summary>
    /// Where each rug was left (a pixel offset from its monitor's top-left) and whether it was left rolled up.
    /// One line per monitor, so the rug is exactly where you put it the next time Windows starts.
    /// </summary>
    internal static class RugPositions
    {
        private static string FilePath => Path.Combine(AppFolders.Roaming, "positions.txt");
        private static string Key(DeskGeometry g) => g.X + "," + g.Y + "," + g.W + "," + g.H;

        public static bool TryGet(DeskGeometry g, out int dx, out int dy, out bool rolled)
        {
            dx = dy = 0;
            rolled = false;
            try
            {
                if (!File.Exists(FilePath)) return false;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2 || kv[0] != Key(g)) continue;
                    string[] v = kv[1].Split(',');
                    if (v.Length < 2 || !int.TryParse(v[0], out dx) || !int.TryParse(v[1], out dy)) return false;
                    rolled = v.Length > 2 && v[2] == "rolled";
                    return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        public static void Set(DeskGeometry g, int dx, int dy, bool rolled)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>();
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                        if (!line.StartsWith(Key(g) + "=")) lines.Add(line);
                lines.Add(Key(g) + "=" + dx + "," + dy + (rolled ? ",rolled" : ""));
                File.WriteAllLines(FilePath, lines);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static class StartupEntry
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "AladdinRug";

        /// <summary>The entry used to be called "DesktopRug" or "AlaaddinRug" (and pointed at the old exe): re-make it under the new name.</summary>
        public static void MigrateOldName()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null) return;
                    foreach (string old in new[] { "DesktopRug", "AlaaddinRug" })
                    {
                        if (k.GetValue(old) == null) continue;
                        k.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
                        k.DeleteValue(old, false);
                    }
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static bool Enabled
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return k?.GetValue(ValueName) != null;
            }
            set
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null) return;
                    if (value) k.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
                    else k.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
