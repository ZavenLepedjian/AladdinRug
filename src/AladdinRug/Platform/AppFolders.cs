using System;
using System.IO;

namespace AladdinRug
{
    /// <summary>
    /// Where the app keeps its settings, its undo lists and its log. The program used to be called "DesktopRug" and then "AlaaddinRug": the
    /// folders from then are moved across the first time the new version runs, so nothing is lost (the undo lists matter most).
    /// </summary>
    internal static class AppFolders
    {
        private const string Name = "AladdinRug";
        private static readonly string[] OldNames = { "AlaaddinRug", "DesktopRug" };

        /// <summary>%APPDATA%\AladdinRug: settings, where the rug lies, the undo lists.</summary>
        public static readonly string Roaming = Folder(Environment.SpecialFolder.ApplicationData);

        /// <summary>%LOCALAPPDATA%\AladdinRug: the log.</summary>
        public static readonly string Local = Folder(Environment.SpecialFolder.LocalApplicationData);

        private static string Folder(Environment.SpecialFolder parent)
        {
            string root = Environment.GetFolderPath(parent);
            string folder = Path.Combine(root, Name);
            foreach (string oldName in OldNames)
            {
                string old = Path.Combine(root, oldName);
                try
                {
                    if (Directory.Exists(old) && !Directory.Exists(folder)) Directory.Move(old, folder);
                }
                catch (IOException) { }                // can't move it: start afresh rather than fail to start
                catch (UnauthorizedAccessException) { }
            }
            return folder;
        }
    }
}
