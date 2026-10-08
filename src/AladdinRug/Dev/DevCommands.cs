using System.Windows;

namespace AladdinRug
{
    /// <summary>
    /// Developer modes: pictures and measurements without touching the real rug. See README, "Developer tools".
    /// <code>
    ///   AladdinRug.exe [--style kilim] --preview flat|roll|rollmid|hold|stuffed out.png
    ///   AladdinRug.exe --sequence up|out folder        (the merchant rolling the rug, frame by frame)
    ///   AladdinRug.exe --merchant out.png              (a sheet of the merchant's poses)
    ///   AladdinRug.exe --simtest folder [scenario]     (cloth scenarios: middle, tent, corner, edge, drag)
    ///   AladdinRug.exe --selftest report.txt           (a temporary rug on the desktop, timing how smoothly it runs)
    /// </code>
    /// </summary>
    internal static class DevCommands
    {
        /// <summary>Runs a developer mode and returns the exit code, or null if the arguments aren't one.</summary>
        public static int? Run(string[] args)
        {
            switch (args[0])
            {
                case "--preview" when args.Length >= 3:
                    new RugWindow(DeskGeometry.Primary()).RenderPreview(args[1], args[2]);
                    return 0;
                case "--sequence" when args.Length >= 3:
                    new RugWindow(DeskGeometry.Primary()).RenderSequence(args[1], args[2]);
                    return 0;
                case "--merchant" when args.Length >= 2:
                    SimDemo.MerchantSheet(args[1]);
                    return 0;
                case "--simtest" when args.Length >= 2:
                    SimDemo.Run(args[1], args.Length > 2 ? args[2] : null);
                    return 0;
                case "--selftest" when args.Length >= 2:
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    RugWindow.RunSelfTest(args[1]);
                    app.Run();
                    return 0;
                default:
                    return null;
            }
        }
    }
}
