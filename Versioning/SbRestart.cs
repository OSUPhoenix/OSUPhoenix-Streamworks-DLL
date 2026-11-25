// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Versioning/SbRestart.cs                         NEW FILE
//
//  Closes Streamer.bot politely and has it reopen by itself.
//
//  WHY THIS IS POSSIBLE:
//    Streamer.bot has no CPH method to exit or restart. But our DLL is
//    loaded INSIDE Streamer.bot's own process, so we can reach its main
//    window and ask it to close — exactly like the user clicking the X.
//    Streamer.bot then runs its normal shutdown (saving everything).
//
//  HOW THE "REOPEN" PART WORKS:
//    DllInstaller's swap script is already waiting for Streamer.bot to
//    exit. Before closing, we drop a "relaunch.flag" file. After the
//    script finishes moving files, it sees the flag and starts
//    Streamer.bot.exe again.
//
//  WHAT WE NEVER DO:
//    Kill the process. If Streamer.bot asks "are you sure?" and the user
//    says no, or it hides in the tray instead of closing, we respect that
//    and tell the user to close it themselves.
//
//  WHY REFLECTION:
//    Streamer.bot's window is WPF. Referencing WPF directly would mean
//    three extra assembly references in the csproj (PresentationFramework,
//    PresentationCore, WindowsBase). Instead we find WPF's Application
//    class at run time from the assemblies Streamer.bot already loaded.
//    If anything isn't there, we fall back to Process.CloseMainWindow().
// ═══════════════════════════════════════════════════════════════════

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace OSWTools.Versioning
{
    public static class SbRestart
    {
        private const string RelaunchFlagName = "relaunch.flag";

        /// <summary>When this file exists, the swap script reopens Streamer.bot.</summary>
        internal static string RelaunchFlagPath
        {
            get { return Path.Combine(DllInstaller.UpdaterFolder, RelaunchFlagName); }
        }

        /// <summary>Full path of the running Streamer.bot.exe.</summary>
        internal static string SbExePath
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().MainModule.FileName;
                }
                catch
                {
                    // MainModule can throw in rare permission cases.
                    return Path.Combine(DllInstaller.SbFolder, "Streamer.bot.exe");
                }
            }
        }

        /// <summary>
        /// Removes a relaunch request left over from an earlier session
        /// (e.g. the user clicked Restart but cancelled SB's exit). Called
        /// at startup so SB never reopens unexpectedly later.
        /// </summary>
        internal static void ClearStaleRelaunchRequest()
        {
            try { if (File.Exists(RelaunchFlagPath)) File.Delete(RelaunchFlagPath); }
            catch { }
        }

        /// <summary>
        /// Asks Streamer.bot to close, and has the swap script reopen it.
        /// Waits up to <paramref name="waitSeconds"/> for SB to exit.
        ///
        /// If SB DOES exit, this method never returns — the process ends
        /// around us, which is the point. If it returns false, SB is still
        /// running (user cancelled, or it went to the tray); the relaunch
        /// request stays, so SB reopens whenever the user closes it.
        ///
        /// Must be called from a background thread (never SB's UI thread),
        /// because it sleeps while waiting.
        /// </summary>
        public static bool CloseAndRelaunch(int waitSeconds = 20)
        {
            File.WriteAllText(RelaunchFlagPath, "relaunch");

            if (!TryCloseWpfMainWindow())
            {
                // Not WPF, or reflection failed: fall back to the plain
                // Windows "close your main window" message.
                try { Process.GetCurrentProcess().CloseMainWindow(); }
                catch { }
            }

            // If SB is shutting down, this thread dies with the process
            // somewhere in this loop. Background threads don't block exit.
            for (int i = 0; i < waitSeconds * 2; i++)
                Thread.Sleep(500);

            return false;   // still here → SB didn't close
        }

        // ── WPF via reflection ────────────────────────────────────────────

        /// <summary>
        /// Equivalent to this WPF code, written without a compile-time
        /// reference to WPF:
        ///
        ///     var app = System.Windows.Application.Current;
        ///     app.Dispatcher.BeginInvoke(new Action(() => app.MainWindow.Close()));
        ///
        /// BeginInvoke matters: WPF windows may only be touched from the UI
        /// thread that created them. The Dispatcher queues our code to run
        /// on that thread.
        /// </summary>
        private static bool TryCloseWpfMainWindow()
        {
            try
            {
                // Only look at assemblies SB already loaded — don't load WPF ourselves.
                Assembly wpf = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "PresentationFramework");
                if (wpf == null) return false;

                Type appType = wpf.GetType("System.Windows.Application");
                object app   = appType == null ? null : appType.GetProperty("Current").GetValue(null);
                if (app == null) return false;

                object dispatcher = appType.GetProperty("Dispatcher").GetValue(app);
                if (dispatcher == null) return false;

                Action closeMain = () =>
                {
                    object win = appType.GetProperty("MainWindow").GetValue(app);
                    if (win != null)
                        win.GetType().GetMethod("Close", Type.EmptyTypes).Invoke(win, null);
                };

                // Dispatcher.BeginInvoke(Delegate method, params object[] args)
                MethodInfo beginInvoke = dispatcher.GetType().GetMethod(
                    "BeginInvoke", new[] { typeof(Delegate), typeof(object[]) });
                if (beginInvoke == null) return false;

                beginInvoke.Invoke(dispatcher, new object[] { closeMain, new object[0] });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
