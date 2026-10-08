using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AladdinRug
{
    internal static class Program
    {
        private const int HotkeyId = 0x5255;           // "RU"
        private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
        private const int WmHotkey = 0x0312;

        // Named events a second copy of the program uses to ask the running rug to do something.
        private const string ToggleSignal = @"Local\AladdinRug.Toggle", SweepSignal = @"Local\AladdinRug.Sweep", PullOutSignal = @"Local\AladdinRug.PullOut",
                             TidySignal = @"Local\AladdinRug.Tidy", UntidySignal = @"Local\AladdinRug.Untidy";

        [STAThread]
        private static int Main(string[] args)
        {
            // --style persian|kilim|shaggy|doormat picks the rug for this run (and for the picture modes).
            int styleAt = Array.IndexOf(args, "--style");
            if (styleAt >= 0 && styleAt + 1 < args.Length)
            {
                if (Enum.TryParse(args[styleAt + 1], true, out RugStyle chosen)) RugStyles.Current = chosen;
                var rest = new List<string>(args);
                rest.RemoveRange(styleAt, 2);
                args = rest.ToArray();
            }

            if (args.Length > 0)
            {
                int? handled = AskRunningRug(args) ?? DevCommands.Run(args);
                if (handled.HasValue) return handled.Value;
            }

            StartupEntry.MigrateOldName();

            // One rug per desktop. Starting the exe again just rolls the one that's already there up or out.
            var toggle = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleSignal);
            var mutex = new Mutex(false, @"Local\AladdinRug.Singleton");
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) { toggle.Set(); return 0; }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>       // a stray error shouldn't take the rug down
            {
                Log.Write("unhandled: " + e.Exception);
                e.Handled = true;
            };
            var rug = new RugSet();
            TrayIcon tray = null;
            bool leaving = false;

            Action quit = () =>
            {
                if (leaving) return;
                leaving = true;
                rug.Quit();
                tray?.Dispose();
                app.Shutdown();
            };

            tray = new TrayIcon(rug, quit);
            rug.ContextRequested += () => tray.ShowMenu();

            void Listen(string signal, Action action)
            {
                var handle = new EventWaitHandle(false, EventResetMode.AutoReset, signal);
                ThreadPool.RegisterWaitForSingleObject(handle, (state, timedOut) => app.Dispatcher.BeginInvoke(action), null, Timeout.Infinite, false);
            }
            ThreadPool.RegisterWaitForSingleObject(toggle, (state, timedOut) => app.Dispatcher.BeginInvoke(new Action(rug.Toggle)), null, Timeout.Infinite, false);
            Listen(SweepSignal, rug.Sweep);
            Listen(PullOutSignal, rug.PullOut);
            Listen(TidySignal, rug.Tidy);
            Listen(UntidySignal, rug.Untidy);

            // Message-only window that listens for Ctrl+Alt+R.
            var hotkeyWindow = new HwndSource(new HwndSourceParameters("AladdinRugHotkey") { ParentWindow = new IntPtr(-3) });
            hotkeyWindow.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WmHotkey && wParam.ToInt32() == HotkeyId) { rug.Toggle(); handled = true; }
                return IntPtr.Zero;
            });
            Native.RegisterHotKey(hotkeyWindow.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, 0x52);   // R

            rug.Begin();
            // once the rug is on screen: sort the desktop and sweep the folders under it (if the user has agreed to that)
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (tray.AskAboutStartupChores()) rug.StartupChores();
            }));
            app.Run();

            Native.UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
            hotkeyWindow.Dispose();
            mutex.ReleaseMutex();
            return 0;
        }

        /// <summary>
        /// --sweep, --pullout, --tidy, --untidy: ask the running rug to do it. If no rug is running, --pullout and --untidy do the
        /// putting-back themselves (so things can always be restored). Null if the arguments aren't one of these.
        /// </summary>
        private static int? AskRunningRug(string[] args)
        {
            string signal;
            switch (args[0])
            {
                case "--sweep": signal = SweepSignal; break;
                case "--pullout": signal = PullOutSignal; break;
                case "--tidy": signal = TidySignal; break;
                case "--untidy": signal = UntidySignal; break;
                default: return null;
            }
            if (EventWaitHandle.TryOpenExisting(signal, out EventWaitHandle running)) { running.Set(); Console.WriteLine("asked the rug"); return 0; }

            if (args[0] == "--pullout")
            {
                int back = SweptStore.PutBack(null, out string why);
                Console.WriteLine(why ?? ("put back " + back + " icons"));
                return 0;
            }
            if (args[0] == "--untidy")
            {
                int back = Organizer.Undo(out string why);
                Console.WriteLine(why ?? ("put back " + back + " files"));
                return 0;
            }
            Console.WriteLine("the rug isn't running");
            return 1;
        }
    }

    /// <summary>The app's log: %LOCALAPPDATA%\AladdinRug\log.txt.</summary>
    internal static class Log
    {
        public static void Write(string message)
        {
            try
            {
                string dir = AppFolders.Local;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "log.txt"), DateTime.Now.ToString("s") + "  " + message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
