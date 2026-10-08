using System;
using System.Collections.Generic;

namespace AladdinRug
{
    /// <summary>
    /// <code>
    ///   SimTest [--style kilim] bench                     how long a dragging frame takes (physics + drawing)
    ///   SimTest [--style kilim] stress out-folder [name]  wild gestures (circles, flick, shake, across): contact sheets, cracks, stretch, shear
    ///   SimTest [--style kilim] scenes out-folder [name]  gentle scenarios (middle, tent, corner, edge, drag) as pictures
    ///   SimTest [--style kilim] dust out-folder           drop an edge and photograph the dust
    ///   SimTest organize scratch-folder                   sort and un-sort made-up files in a scratch folder (exit code = failures)
    /// </code>
    /// </summary>
    internal static class SimTestMain
    {
        [STAThread]
        private static int Main(string[] argv)
        {
            var list = new List<string>(argv);
            int at = list.IndexOf("--style");
            if (at >= 0 && at + 1 < list.Count)
            {
                if (Enum.TryParse(list[at + 1], true, out RugStyle style)) RugStyles.Current = style;
                list.RemoveRange(at, 2);
            }
            string[] args = list.ToArray();
            string command = args.Length > 0 ? args[0] : "";
            string arg1 = args.Length > 1 ? args[1] : null, arg2 = args.Length > 2 ? args[2] : null;

            switch (command)
            {
                case "bench": SimDemo.Bench(); return 0;
                case "stress" when arg1 != null: SimDemo.Stress(arg1, arg2); return 0;
                case "scenes" when arg1 != null: SimDemo.Run(arg1, arg2); return 0;
                case "dust" when arg1 != null: SimDemo.DustDemo(arg1); return 0;
                case "organize" when arg1 != null: return OrganizeTest.Run(arg1);
                default:
                    Console.WriteLine("usage: SimTest [--style persian|kilim|shaggy|doormat] bench | stress <out> [gesture] | scenes <out> [scenario] | dust <out> | organize <scratch-folder>");
                    return 2;
            }
        }
    }
}
