// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Versioning/OSWStartup.cs                        NEW FILE
//
//  The once-per-session startup work, callable from ANY entry point.
//
//  WHY THIS EXISTS:
//    The DLL-location check used to live inside the OSWLib constructor.
//    But some tools never create an OSWLib — OSW Panels, for example,
//    calls the static OSWPanels.Handle(CPH, args). For those, the check
//    never ran, so users with only those tools never got the move.
//
//    Now the work lives here, behind one method. OSWLib's constructor
//    calls it, and so does every static entry point:
//
//        OSWStartup.EnsureStarted(cph);
//
//  WHAT IT DOES (first call per Streamer.bot session only):
//    1. Makes sure the OSWData folder exists.
//    2. Checks where OSWTools.dll lives (DllInstaller.CheckLocation) and
//       schedules the move / reference fix if needed.
//    3. Shows the "Restart Needed" popup if a restart would finish it.
//  Every later call is a single bool check — effectively free.
//
//  TIP FOR TOOL ACTIONS:
//    Streamer.bot calls a C# sub-action's  public void Init()  when it
//    compiles the code at startup, before any trigger fires. A tool that
//    adds
//        public void Init() { OSWTools.OSWStartup.EnsureStarted(CPH); }
//    gets the check (and popup) right at Streamer.bot launch.
// ═══════════════════════════════════════════════════════════════════

using System;
using Streamer.bot.Plugin.Interface;
using OSWTools.Versioning;

namespace OSWTools
{
    public static class OSWStartup
    {
        private static bool _started = false;
        private static readonly object _lock = new object();

        /// <summary>True once the startup work has run this session.</summary>
        public static bool HasStarted
        {
            get { return _started; }
        }

        /// <summary>
        /// Runs the once-per-session startup work the first time it's called.
        /// Thread-safe, never throws. Call it at the top of any entry point
        /// that doesn't create an OSWLib.
        /// </summary>
        public static void EnsureStarted(IInlineInvokeProxy cph)
        {
            // Fast path: one bool read, no lock.
            if (_started || cph == null) return;

            lock (_lock)
            {
                // Double-check: another thread may have finished while we waited.
                if (_started) return;

                // Set FIRST, so even if something below throws we never run
                // this twice or retry on every action.
                _started = true;

                try
                {
                    OSWTools.Data.FileManager.EnsureRootFolder();
                }
                catch (Exception ex)
                {
                    SafeLog(cph, true, "[OSWTools] Could not create the OSWData folder: " + ex.Message);
                }

                try
                {
                    // Read as a string: an unset global comes back null/empty,
                    // which we treat as "on". Only an explicit false turns it off.
                    string autoMove = cph.GetGlobalVar<string>("osw_AutoRelocateDll", true);
                    bool   enabled  = !string.Equals(autoMove, "false", StringComparison.OrdinalIgnoreCase);

                    RestartReason reason = DllInstaller.CheckLocation(
                        enabled,
                        msg => SafeLog(cph, false, msg),
                        msg => SafeLog(cph, true, msg),
                        (title, msg) =>
                        {
                            try   { cph.ShowToastNotification(title, msg); }
                            catch { SafeLog(cph, false, "[OSWTools] " + title + ": " + msg); }
                        });

                    if (reason != RestartReason.None)
                        SafeLog(cph, false, "[OSWTools] Showing popup: " + reason);

                    DllInstaller.ShowRestartPromptAsync(reason);
                }
                catch (Exception ex)
                {
                    SafeLog(cph, true, "[OSWTools] Startup check failed: " + ex.Message);
                }
            }
        }

        private static void SafeLog(IInlineInvokeProxy cph, bool warn, string msg)
        {
            try
            {
                if (warn) cph.LogWarn(msg);
                else      cph.LogInfo(msg);
            }
            catch { }
        }
    }
}
