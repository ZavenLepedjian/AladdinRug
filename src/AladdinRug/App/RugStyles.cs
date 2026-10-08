using System;
using System.IO;
using System.Windows.Media;

namespace AladdinRug
{
    internal enum RugStyle { Persian, Kilim, Shaggy, Doormat }

    /// <summary>How heavy and how stiff a rug is: the numbers the cloth physics uses.</summary>
    internal sealed class ClothFeel
    {
        public float Gravity = 3600f;       // how hard it falls
        public float Friction = 0.87f;      // grip on the floor (lower = grips more)
        public float Air = 0.90f;           // drag while lifted (lower = floats more slowly)
        public float Squash = 0.70f;        // how hard the weave pushes back when squeezed
        public float BendSquash = 0.75f;    // ...and how much it resists sharp folds
        public float BendStretch = 1.0f;
    }

    /// <summary>Everything that makes one kind of rug itself: its size, picture, weave, weight and the roll it makes.</summary>
    internal sealed class StyleSpec
    {
        public RugStyle Style;
        public string Name, Description;
        public double WidthFactor = 1, HeightFactor = 1;      // relative to the default rug size
        public ClothFeel Feel = new ClothFeel();
        public Color[] RollColors;                            // the bands seen on the rolled-up rug
        public Color BackColor;                               // the underside
        public float BackFlat = 0.38f;                        // how much of the pattern shows through on the underside
        public double AladdinX = 0.5, AladdinY = 0.5, AladdinSize = 1.45;   // where he sits (share of the rug) and how big

        // woven texture (1 = the Persian rug's)
        public float Grid = 1, LinesX = 1, LinesY = 1, Grain = 1, Fuzz = 1, Abrash = 1, Lump = 1, Folds = 1, Streak = 0;
    }

    internal static class RugStyles
    {
        private static Color C(int v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

        public static readonly StyleSpec[] All =
        {
            new StyleSpec
            {
                Style = RugStyle.Persian, Name = "Persian", Description = "Heavy wool, knotted fringe",
                RollColors = new[] { C(0x7B1C27), C(0x1E2D55), C(0xEADFC0), C(0x7B1C27), C(0xC8993C), C(0x1E2D55), C(0x5A111B), C(0xEADFC0), C(0x1E2D55), C(0x7B1C27) },
                BackColor = C(0xC8AC82),
            },
            new StyleSpec
            {
                Style = RugStyle.Kilim, Name = "Kilim", Description = "Thin flat-weave: light, floppy, slides about",
                Feel = new ClothFeel { Gravity = 3000f, Friction = 0.91f, Air = 0.87f, Squash = 0.55f, BendSquash = 0.50f, BendStretch = 0.9f },
                RollColors = new[] { C(0xB4492F), C(0xE6D9B6), C(0x23325C), C(0xD1A238), C(0xB4492F), C(0x6D7440), C(0xE6D9B6), C(0x8A2A25), C(0x23325C), C(0xB4492F) },
                BackColor = C(0xBAA080), BackFlat = 0.6f,
                Grid = 0.5f, LinesX = 0.4f, LinesY = 1.9f, Grain = 1.1f, Fuzz = 0.5f, Abrash = 1.5f, Lump = 1.15f, Folds = 1.2f,
            },
            new StyleSpec
            {
                Style = RugStyle.Shaggy, Name = "Shaggy", Description = "Deep pile: heavy, grippy, hard to fold",
                Feel = new ClothFeel { Gravity = 4100f, Friction = 0.80f, Air = 0.92f, Squash = 0.82f, BendSquash = 0.90f },
                RollColors = new[] { C(0xE3D9C4), C(0xC9BCA2), C(0xEFE7D6), C(0xD2C6AD), C(0xE3D9C4), C(0xBFB295), C(0xEFE7D6), C(0xD8CDB6), C(0xC9BCA2), C(0xE3D9C4) },
                BackColor = C(0x9C9382), BackFlat = 0.15f,
                Lump = 0.7f, Folds = 0.6f,
            },
            new StyleSpec
            {
                Style = RugStyle.Doormat, Name = "Doormat", Description = "Stiff coir: small, grippy, says what it thinks",
                WidthFactor = 0.74, HeightFactor = 0.62,
                Feel = new ClothFeel { Gravity = 3700f, Friction = 0.78f, Air = 0.91f, Squash = 0.95f, BendSquash = 1.0f },
                RollColors = new[] { C(0x8A6A43), C(0x6B4F2F), C(0x9A7A50), C(0x2B1D12), C(0x8A6A43), C(0x7A5C38), C(0x9A7A50), C(0x6B4F2F), C(0x2B1D12), C(0x8A6A43) },
                BackColor = C(0x2E2B28), BackFlat = 0.1f,
                AladdinX = 0.72, AladdinY = 0.58, AladdinSize = 0.9,
                Grid = 0, LinesX = 0, LinesY = 0, Grain = 3.2f, Fuzz = 2.2f, Abrash = 1.4f, Lump = 0.9f, Folds = 0.4f, Streak = 0.30f,
            },
        };

        public static StyleSpec Get(RugStyle s) => All[(int)s];

        /// <summary>The rug in use. Remembered between runs.</summary>
        public static RugStyle Current { get; set; } = Load();

        /// <summary>Is Aladdin sitting on the rug? Remembered between runs.</summary>
        public static bool Aladdin { get; set; } = LoadFlag("aladdin");

        /// <summary>Sort the desktop into folders whenever the rug starts? Null until the user has been asked.</summary>
        public static bool? AutoTidy { get; set; } = LoadOptional("autotidy");

        /// <summary>After sorting at start-up, also sweep the folders under the rug? Off unless the user turns it on.</summary>
        public static bool AutoSweep { get; set; } = LoadOptional("autosweep") == true;

        private static bool? LoadOptional(string key)
        {
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                        if (line.StartsWith(key + "=")) return line.Substring(key.Length + 1).Trim() != "off";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>Does a merchant walk in to roll the rug up and out? Remembered between runs.</summary>
        public static bool Roller { get; set; } = LoadFlag("roller");

        private static string FilePath => Path.Combine(AppFolders.Roaming, "settings.txt");

        private static RugStyle Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                        if (line.StartsWith("style=") && Enum.TryParse(line.Substring(6).Trim(), true, out RugStyle s)) return s;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return RugStyle.Persian;
        }

        private static bool LoadFlag(string key)
        {
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                        if (line.StartsWith(key + "=")) return line.Substring(key.Length + 1).Trim() != "off";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return true;
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, "style=" + Current + Environment.NewLine + "aladdin=" + (Aladdin ? "on" : "off") + Environment.NewLine
                                    + "roller=" + (Roller ? "on" : "off") + Environment.NewLine
                                    + (AutoTidy.HasValue ? "autotidy=" + (AutoTidy.Value ? "on" : "off") + Environment.NewLine : "")
                                    + "autosweep=" + (AutoSweep ? "on" : "off") + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
