// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Versioning/DllInstaller.cs                      NEW FILE
//
//  Knows WHERE OSWTools.dll should live and moves it there.
//
//  WHY THIS EXISTS:
//    Streamer.bot's creator asks that every non-official DLL live in
//    the "dlls" subfolder of the Streamer.bot folder:
//
//        <Streamer.bot folder>\Streamer.bot.exe        ← SB itself
//        <Streamer.bot folder>\dlls\OSWTools.dll       ← where we belong
//        <Streamer.bot folder>\OSWData\...             ← our data (unchanged)
//
//    Older installs put OSWTools.dll next to Streamer.bot.exe. This
//    class moves it into dlls\ automatically, so users don't have to.
//
//  THE PROBLEM WE HAVE TO WORK AROUND:
//    While Streamer.bot is running, Windows locks the loaded
//    OSWTools.dll — it can't be moved, deleted or overwritten. So we
//    can't move it "now". Instead we:
//
//      1. Create the dlls\ folder right away (proves we can write there).
//      2. Write a small .bat script into OSWData\Updater\.
//      3. Start that script hidden. It WAITS until Streamer.bot closes
//         (it watches SB's process ID), then does the file work.
//      4. Next time SB starts, the DLL loads from dlls\ and we show
//         a "moved successfully" toast once.
//
//  FIXING ACTION REFERENCES (NEW):
//    Streamer.bot saves each C# sub-action's references as FULL paths,
//    e.g. C:\Streamer.bot\OSWTools.dll. Moving the DLL breaks those, so
//    after the DLL is in place the script also runs OSW-FixReferences.ps1
//    (embedded in this DLL). It rewrites the saved paths in
//    <SB folder>\data\*.json to dlls\OSWTools.dll, with a backup first.
//    This has to happen while SB is closed — SB keeps actions in memory
//    and writes them back on save, which would undo the edit.
//
//  ONE SCRIPT FOR BOTH JOBS:
//    The same script also installs downloaded updates. When it runs it
//    checks, in this order:
//      a) Is a downloaded update waiting?  → copy it into dlls\
//      b) Otherwise, are we still in the old spot? → copy us into dlls\
//      c) If dlls\OSWTools.dll now exists → delete the old root copy
//         and point all saved action references at it
//    Because it decides AT RUN TIME (after SB closes), an update
//    downloaded later in the same session is still picked up — we
//    only ever need to launch one script per session.
//
//  PLACEMENT:
//    Versioning/ → picked up by the existing wildcard compile glob.
//    No csproj change needed.
//
//  RESTART PROMPT (NEW):
//    CheckLocation() now returns WHY a restart is needed. OSWLib then
//    calls ShowRestartPromptAsync(), which shows DllMoveForm. If the user
//    clicks "Restart Streamer.bot", SbRestart closes SB politely and the
//    swap script reopens it once the files are moved.
//
//  FAST PATH (NEW):
//    Scanning Streamer.bot's saved actions costs a file read. So after
//    one clean check we write OSWData\Updater\verified.txt
//    ("<DLL version>|<DLL path>"). From then on, if the DLL is in dlls\,
//    no old root copy exists, and verified.txt still matches, the check
//    is three quick file-system lookups and returns. A new DLL version or
//    a moved Streamer.bot folder re-checks once, automatically.
//
//  GIVING UP GRACEFULLY:
//    CHANGED: only REAL failures count. The swap script writes its outcome
//    ("ok" or "failed") to OSWData\Updater\last-run.txt. On the next start:
//      failed       → one more failed run
//      ok           → counter back to 0 (a problem now is a new one, e.g.
//                     the DLL was put back in the root folder by hand)
//      no file      → the script never ran (PC shut down with SB open,
//                     etc.) — not counted either way
//    After MaxFailedRuns real failures we stop the restart popup and show
//    a one-time "manual step" dialog instead.
//    (The first version counted every session that started with a problem,
//    so testing — moving the DLL back by hand — silenced the popup.)
//
//  OPT-OUT:
//    Set the persisted global  osw_AutoRelocateDll = false  to stop the
//    automatic move (updates still install into dlls\).
// ═══════════════════════════════════════════════════════════════════

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace OSWTools.Versioning
{
    /// <summary>Why Streamer.bot needs a restart (if at all).</summary>
    public enum RestartReason
    {
        /// <summary>Nothing pending — no restart needed.</summary>
        None,
        /// <summary>OSWTools.dll moves into dlls\ when SB closes.</summary>
        DllMoving,
        /// <summary>The DLL is already in dlls\, but saved actions still point elsewhere.</summary>
        ReferencesOnly,
        /// <summary>NEW: the automatic move failed several times — show manual move steps once.</summary>
        ManualMoveNeeded,
        /// <summary>NEW: the automatic reference fix failed several times — show manual steps once.</summary>
        ManualReferencesNeeded
    }

    public static class DllInstaller
    {
        // ── File and folder names ─────────────────────────────────────────
        public const string DllFileName     = "OSWTools.dll";
        public const string DllsFolderName  = "dlls";
        private const string PendingName    = "OSWTools.dll.pending";
        private const string MovedFlagName  = "relocated.flag";
        private const string LogName        = "install.log";
        private const string FixScriptName  = "OSW-FixReferences.ps1";
        private const string VerifiedName   = "verified.txt";        // NEW: fast-path marker
        private const string FailedRunsName = "failed-runs.txt";     // CHANGED: counts real failures only
        private const string LastRunName    = "last-run.txt";        // NEW: swap script writes ok / failed
        private const string LegacyAttempts = "attempts.txt";        // old counter from the first version — removed on sight
        private const string HelpShownName  = "manual-help.shown";   // manual dialog shown once

        /// <summary>
        /// Real failed runs of the swap script before we switch from the
        /// restart popup to the manual-steps dialog. Runs that never happened
        /// (script killed by a PC shutdown) don't count, so 2 is enough.
        /// </summary>
        public const int MaxFailedRuns = 2;


        // Only one swap script per Streamer.bot session (see header).
        private static bool _scriptLaunched = false;
        private static readonly object _lock = new object();

        // ── Paths ─────────────────────────────────────────────────────────
        // BaseDirectory is the folder Streamer.bot.exe runs from. It does
        // NOT change when our DLL moves, so OSWData stays where it is.

        /// <summary>The Streamer.bot folder (where Streamer.bot.exe lives).</summary>
        public static string SbFolder
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>&lt;SB folder&gt;\dlls</summary>
        public static string DllsFolder
        {
            get { return Path.Combine(SbFolder, DllsFolderName); }
        }

        /// <summary>Where OSWTools.dll SHOULD be: &lt;SB folder&gt;\dlls\OSWTools.dll</summary>
        public static string TargetDllPath
        {
            get { return Path.Combine(DllsFolder, DllFileName); }
        }

        /// <summary>The old location: &lt;SB folder&gt;\OSWTools.dll</summary>
        public static string LegacyDllPath
        {
            get { return Path.Combine(SbFolder, DllFileName); }
        }

        /// <summary>
        /// Where the DLL that is running right now was loaded from.
        /// Assembly.Location can be empty if a host loads DLLs from memory,
        /// so we fall back to the legacy path in that case.
        /// </summary>
        public static string LoadedDllPath
        {
            get
            {
                string loc = typeof(DllInstaller).Assembly.Location;
                return string.IsNullOrEmpty(loc) ? LegacyDllPath : loc;
            }
        }

        /// <summary>True when the running DLL is already in dlls\.</summary>
        public static bool IsInDllsFolder
        {
            get { return SamePath(LoadedDllPath, TargetDllPath); }
        }

        /// <summary>&lt;SB folder&gt;\data — where Streamer.bot saves actions.</summary>
        public static string SbDataFolder
        {
            get { return Path.Combine(SbFolder, "data"); }
        }

        /// <summary>OSWData\Updater — holds the pending download, the script and the log.</summary>
        internal static string UpdaterFolder
        {
            get { return Path.Combine(SbFolder, "OSWData", "Updater"); }
        }

        /// <summary>Where a downloaded update waits until Streamer.bot closes.</summary>
        internal static string PendingPath
        {
            get { return Path.Combine(UpdaterFolder, PendingName); }
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Called once per session from OSWLib's static init.
        ///  • If the last move just finished, reports success (log + toast).
        ///  • If the DLL is still in the old spot, or a stray old copy is
        ///    left behind, creates dlls\ and schedules the move.
        /// Never throws — problems are reported through 'log'.
        /// </summary>
        /// <param name="autoRelocate">The osw_AutoRelocateDll global.</param>
        /// <param name="log">Writes an info line to the SB log.</param>
        /// <param name="warn">Writes a warning line to the SB log.</param>
        /// <param name="toast">Shows a Windows toast (title, message).</param>
        /// <returns>
        /// Why a restart is needed (CHANGED: was void). OSWLib passes this to
        /// ShowRestartPromptAsync() so the user gets a popup, not just a log line.
        /// </returns>
        public static RestartReason CheckLocation(bool autoRelocate,
                                         Action<string> log,
                                         Action<string> warn,
                                         Action<string, string> toast)
        {
            try
            {
                Directory.CreateDirectory(UpdaterFolder);
                AdoptLegacyPending();
                SbRestart.ClearStaleRelaunchRequest();   // SB has restarted, so any old request is spent
                RemoveLegacyCounter();                   // NEW: undo the first version's over-counting
                string lastRun = TakeLastRunResult();    // NEW: "ok", "failed", or null (script didn't run)

                // ── 1. Did a previous move just succeed? ─────────────────
                string flag = Path.Combine(UpdaterFolder, MovedFlagName);
                if (File.Exists(flag) && IsInDllsFolder)
                {
                    File.Delete(flag);
                    log("[OSWTools] OSWTools.dll now lives in " + DllsFolder);
                    toast("OSWTools moved",
                          "OSWTools.dll now lives in the Streamer.bot \"dlls\" folder.");
                }

                // ── 2. FAST PATH (NEW): already verified? ────────────────
                //  Three cheap checks, no reading of SB's action files.
                bool notInDlls = !IsInDllsFolder;
                bool strayCopy = !notInDlls && File.Exists(LegacyDllPath);
                if (!notInDlls && !strayCopy && IsVerified())
                {
                    log("[OSWTools] OSWTools.dll location OK (" + TargetDllPath + ")");   // NEW: one line per session
                    return RestartReason.None;
                }

                // NEW: say what we're checking, so the SB log shows the check ran.
                log("[OSWTools] Checking OSWTools.dll location. Loaded from: " + LoadedDllPath);

                // ── 3. Full check: scan saved actions ────────────────────
                int staleRefs = CountStaleReferences();

                if (!notInDlls && !strayCopy && staleRefs == 0)
                {
                    // Everything is correct. Remember that, so every future
                    // session takes the fast path above.
                    MarkVerified();
                    ResetAttempts();
                    log("[OSWTools] OSWTools.dll location verified: " + TargetDllPath);
                    return RestartReason.None;
                }

                ClearVerified();   // something's off — re-check fully next session too

                if (!autoRelocate)
                {
                    if (notInDlls)
                        log("[OSWTools] OSWTools.dll is in the Streamer.bot root folder. "
                            + "Auto-move is off (osw_AutoRelocateDll = false). "
                            + "Move it to " + DllsFolder + " by hand when convenient.");
                    return RestartReason.None;
                }

                // Create dlls\ now. If this fails (e.g. SB is installed in
                // Program Files without admin rights) we stop here, before
                // scheduling anything, and leave the DLL where it works.
                Directory.CreateDirectory(DllsFolder);

                if (!ScheduleSwapOnExit()) return RestartReason.None;

                if (notInDlls)
                    log("[OSWTools] OSWTools.dll will move to the \"dlls\" folder when Streamer.bot closes.");
                else if (strayCopy)
                    log("[OSWTools] An old OSWTools.dll copy in the Streamer.bot folder will be removed when Streamer.bot closes.");
                if (staleRefs > 0)
                    log("[OSWTools] " + staleRefs + " saved action reference(s) to OSWTools.dll will be updated to the \"dlls\" folder when Streamer.bot closes.");

                // A stray root copy alone doesn't need a restart — it's
                // just tidied up whenever SB next closes. Not an "attempt".
                if (!notInDlls && staleRefs == 0) return RestartReason.None;

                // ── 4. Count REAL failures; give up on popups after a few ─
                int failed = ReadFailedRuns();
                if (lastRun == "failed")
                {
                    failed++;
                    AppendInstallLog("Last run failed (" + failed + " of " + MaxFailedRuns + " allowed)");
                }
                else if (lastRun == "ok")
                {
                    failed = 0;   // last run worked — this is a fresh problem
                    ClearHelpShown();
                }
                WriteFailedRuns(failed);

                if (failed < MaxFailedRuns)
                    return notInDlls ? RestartReason.DllMoving : RestartReason.ReferencesOnly;

                warn("[OSWTools] The automatic " + (notInDlls ? "move of OSWTools.dll" : "update of action references")
                     + " failed " + failed + " times. "
                     + "See " + Path.Combine(UpdaterFolder, LogName) + " for details.");

                // Show the manual-steps dialog once, then only log.
                string helpShown = Path.Combine(UpdaterFolder, HelpShownName);
                if (File.Exists(helpShown)) return RestartReason.None;
                File.WriteAllText(helpShown, DateTime.Now.ToString("u"));
                return notInDlls ? RestartReason.ManualMoveNeeded : RestartReason.ManualReferencesNeeded;
            }
            catch (Exception ex)
            {
                warn("[OSWTools] Could not move OSWTools.dll to the \"dlls\" folder: " + ex.Message
                     + " — it will keep working from where it is.");
                return RestartReason.None;
            }
        }

        // ── NEW: install.log writer (safe from any thread) ────────────────

        private static readonly object _logLock = new object();

        /// <summary>
        /// Adds a timestamped line to OSWData\Updater\install.log — the same
        /// file the swap script and OSW-FixReferences.ps1 write to, so one
        /// file tells the whole story. Never throws.
        /// </summary>
        internal static void AppendInstallLog(string message)
        {
            try
            {
                lock (_logLock)
                {
                    Directory.CreateDirectory(UpdaterFolder);
                    File.AppendAllText(Path.Combine(UpdaterFolder, LogName),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [dll] " + message + Environment.NewLine);
                }
            }
            catch { }
        }

        // ── NEW: fast-path marker and attempt counter ─────────────────────
        //  Small text files in OSWData\Updater. Any read/write problem is
        //  treated as "not verified" / "0 attempts", which just means we do
        //  the full check — never a wrong "all good".

        /// <summary>What verified.txt must contain: DLL version + DLL path.</summary>
        private static string VerifiedStamp
        {
            get { return OSWVersion.Current + "|" + TargetDllPath; }
        }

        private static bool IsVerified()
        {
            try
            {
                string path = Path.Combine(UpdaterFolder, VerifiedName);
                return File.Exists(path)
                    && string.Equals(File.ReadAllText(path).Trim(), VerifiedStamp, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void MarkVerified()
        {
            try { File.WriteAllText(Path.Combine(UpdaterFolder, VerifiedName), VerifiedStamp); }
            catch { }
        }

        private static void ClearVerified()
        {
            try { File.Delete(Path.Combine(UpdaterFolder, VerifiedName)); }   // no error if missing
            catch { }
        }

        private static int ReadFailedRuns()
        {
            try
            {
                int n;
                string path = Path.Combine(UpdaterFolder, FailedRunsName);
                return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out n) ? n : 0;
            }
            catch { return 0; }
        }

        private static void WriteFailedRuns(int n)
        {
            try
            {
                string path = Path.Combine(UpdaterFolder, FailedRunsName);
                if (n <= 0) File.Delete(path);          // no file = 0, keeps the folder tidy
                else        File.WriteAllText(path, n.ToString());
            }
            catch { }
        }

        private static void ClearHelpShown()
        {
            try { File.Delete(Path.Combine(UpdaterFolder, HelpShownName)); }
            catch { }
        }

        /// <summary>Clears the counter and the "manual help shown" flag once things are fixed.</summary>
        private static void ResetAttempts()
        {
            WriteFailedRuns(0);
            ClearHelpShown();
        }

        /// <summary>
        /// NEW: reads what the swap script wrote about its last run, then
        /// deletes the file so each run is only counted once.
        /// Returns "ok", "failed", or null if the script never got to run.
        /// </summary>
        private static string TakeLastRunResult()
        {
            try
            {
                string path = Path.Combine(UpdaterFolder, LastRunName);
                if (!File.Exists(path)) return null;
                string result = File.ReadAllText(path).Trim().ToLowerInvariant();
                File.Delete(path);
                return result == "ok" || result == "failed" ? result : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// NEW: the first version counted EVERY session that started with a
        /// problem, which wrongly switched the popup off during testing. If
        /// its attempts.txt exists, delete it and the "help shown" flag so
        /// the popup works again.
        /// </summary>
        private static void RemoveLegacyCounter()
        {
            try
            {
                string legacy = Path.Combine(UpdaterFolder, LegacyAttempts);
                if (!File.Exists(legacy)) return;
                File.Delete(legacy);
                ClearHelpShown();
                AppendInstallLog("Removed old attempts.txt (over-counted in an earlier build)");
            }
            catch { }
        }

        /// <summary>
        /// NEW: Shows the "Restart Needed" popup on its own STA thread and
        /// returns immediately, so the tool that triggered it keeps running.
        /// If the user picks "Restart Streamer.bot", closes SB politely and
        /// the swap script reopens it. Does nothing for RestartReason.None.
        ///
        /// No CPH calls happen on the popup thread (CPH isn't thread-safe);
        /// CHANGED: problems go to OSWData\Updater\install.log instead of
        /// OSWLogger, which stays silent unless something initialised it.
        /// </summary>
        public static void ShowRestartPromptAsync(RestartReason reason)
        {
            if (reason == RestartReason.None) return;

            var thread = new Thread(() =>
            {
                try
                {
                    AppendInstallLog("Showing popup: " + reason);
                    bool restart;
                    using (var form = new DllMoveForm(reason))
                    {
                        form.ShowDialog();
                        restart = form.RestartChosen;   // always false for the manual-steps dialog
                    }
                    AppendInstallLog(restart ? "User chose: Restart Streamer.bot" : "User closed the popup");
                    if (!restart) return;

                    // Our popup is closed by now, so it can't be mistaken
                    // for SB's main window. If SB exits, this thread ends
                    // with it and CloseAndRelaunch never returns.
                    if (!SbRestart.CloseAndRelaunch())
                    {
                        System.Windows.Forms.MessageBox.Show(
                            "Streamer.bot didn't close — it may have gone to the system tray, "
                            + "or the exit was cancelled.\r\n\r\n"
                            + "Close Streamer.bot yourself when you're ready. "
                            + "It will reopen by itself once the move is done.",
                            "OSW Tools — Restart Needed",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Information,
                            System.Windows.Forms.MessageBoxDefaultButton.Button1,
                            System.Windows.Forms.MessageBoxOptions.DefaultDesktopOnly);   // keeps it on top
                    }
                }
                catch (Exception ex)
                {
                    AppendInstallLog("Restart popup failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);   // WinForms needs STA
            thread.IsBackground = true;                     // never keeps SB alive on exit
            thread.Start();
        }

        /// <summary>
        /// Writes and starts the swap script (once per session). The script
        /// waits for Streamer.bot to close, then installs a pending update
        /// and/or moves the DLL into dlls\. Returns false only if the script
        /// could not be written or started.
        /// </summary>
        public static bool ScheduleSwapOnExit()
        {
            lock (_lock)
            {
                if (_scriptLaunched) return true;   // already waiting for exit

                Directory.CreateDirectory(UpdaterFolder);
                Directory.CreateDirectory(DllsFolder);

                int    pid        = Process.GetCurrentProcess().Id;
                string scriptPath = Path.Combine(UpdaterFolder, "osw_install_" + pid + ".bat");

                // Batch files are read as the ANSI code page by cmd.exe.
                // Encoding.Default matches that on Windows, so folders with
                // accented characters in their names still work.
                string fixScript = ExtractFixScript();   // NEW: may be null
                File.WriteAllText(scriptPath, BuildScript(pid, fixScript), Encoding.Default);

                Process.Start(new ProcessStartInfo
                {
                    FileName        = "cmd.exe",
                    Arguments       = "/c \"\"" + scriptPath + "\"\"",
                    WindowStyle     = ProcessWindowStyle.Hidden,
                    CreateNoWindow  = true,
                    UseShellExecute = false
                });

                _scriptLaunched = true;
                return true;
            }
        }

        /// <summary>
        /// Counts saved action references to OSWTools.dll that don't point at
        /// dlls\OSWTools.dll yet. Reads SB's data\*.json while SB is running —
        /// read-only, with FileShare.ReadWrite so we never block SB from saving.
        /// Returns 0 if the files can't be read or hold no such references.
        /// </summary>
        public static int CountStaleReferences()
        {
            if (!Directory.Exists(SbDataFolder)) return 0;

            string wanted = "\"" + TargetDllPath.Replace("\\", "\\\\") + "\"";
            int stale = 0;

            foreach (string file in Directory.GetFiles(SbDataFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                    {
                        text = sr.ReadToEnd();
                    }

                    foreach (string value in FindDllReferences(text))
                    {
                        if (!string.Equals(value, wanted, StringComparison.OrdinalIgnoreCase))
                            stale++;
                    }
                }
                catch
                {
                    // A file we can't read just doesn't count.
                }
            }
            return stale;
        }

        /// <summary>
        /// Finds every JSON string value in <paramref name="text"/> that is a
        /// path ending in \OSWTools.dll or /OSWTools.dll, and returns each one
        /// WITH its quotes, exactly as it appears in the file.
        ///
        /// CHANGED (was a regex): a regex over the whole file tried a match at
        /// every quote mark — ~6 s on a 22 MB actions file in testing. This
        /// jumps straight to each "OSWTools.dll" with IndexOf, then steps back
        /// to the opening quote. File paths can't contain " characters, so
        /// the nearest quote to the left is always where the value starts.
        /// OSW-FixReferences.ps1 uses the same steps.
        /// </summary>
        internal static System.Collections.Generic.List<string> FindDllReferences(string text)
        {
            const string Needle = "OSWTools.dll\"";   // file name + closing quote
            var found = new System.Collections.Generic.List<string>();

            int pos = 0;
            while (true)
            {
                int hit = text.IndexOf(Needle, pos, StringComparison.OrdinalIgnoreCase);
                if (hit < 0) break;
                pos = hit + Needle.Length;              // next search starts after this one

                // Must be a path separator right before the name:
                //   "/"  or  "\\" (one backslash, written twice in JSON).
                // This skips values like "NotOSWTools.dll" or a bare "OSWTools.dll".
                bool slash     = hit >= 1 && text[hit - 1] == '/';
                bool backslash = hit >= 2 && text[hit - 1] == '\\' && text[hit - 2] == '\\';
                if (!slash && !backslash) continue;

                int open = text.LastIndexOf('"', hit - 1);
                if (open < 0) continue;

                // A quote right after a backslash is an escaped quote inside
                // some other text, not the start of a value — skip it.
                if (open >= 1 && text[open - 1] == '\\') continue;

                found.Add(text.Substring(open, pos - open));
            }
            return found;
        }

        // ── Internals ─────────────────────────────────────────────────────

        /// <summary>
        /// Copies the embedded OSW-FixReferences.ps1 out of this DLL into
        /// OSWData\Updater so the swap script can run it. Returns the path,
        /// or null if the resource is missing (the swap still works, it
        /// just skips the reference fix).
        ///
        /// Resource names are "RootNamespace.Folder.FileName", but rather than
        /// hard-code that we look for any name ending in the file name.
        /// </summary>
        private static string ExtractFixScript()
        {
            var asm = typeof(DllInstaller).Assembly;
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith(FixScriptName, StringComparison.OrdinalIgnoreCase)) continue;

                string path = Path.Combine(UpdaterFolder, FixScriptName);
                using (Stream src = asm.GetManifestResourceStream(name))
                using (FileStream dst = File.Create(path))
                {
                    src.CopyTo(dst);
                }
                return path;
            }
            return null;
        }

        /// <summary>
        /// Older DLL versions downloaded updates to OSWTools.dll.pending next
        /// to the DLL. If one is lying around, move it to the new spot so the
        /// swap script picks it up. (A .pending file isn't locked — only the
        /// loaded .dll is — so this move works while SB runs.)
        /// </summary>
        private static void AdoptLegacyPending()
        {
            string legacy = Path.Combine(SbFolder, PendingName);
            if (!File.Exists(legacy) || File.Exists(PendingPath)) return;
            File.Move(legacy, PendingPath);
        }

        /// <summary>
        /// Builds the .bat text. Paths are baked in now (we know them);
        /// the decisions about WHAT to copy happen when the script runs.
        /// </summary>
        private static string BuildScript(int pid, string fixScript)
        {
            string loaded  = LoadedDllPath;
            string target  = TargetDllPath;
            string legacy  = LegacyDllPath;
            bool   moveOld = !SamePath(loaded, target);   // are we running from outside dlls\?

            var s = new StringBuilder();
            s.AppendLine("@echo off");
            s.AppendLine("setlocal");
            s.AppendLine("set \"PID=" + pid + "\"");
            s.AppendLine("set \"PENDING=" + PendingPath + "\"");
            s.AppendLine("set \"TARGET=" + target + "\"");
            s.AppendLine("set \"LOADED=" + loaded + "\"");
            s.AppendLine("set \"LEGACY=" + legacy + "\"");
            s.AppendLine("set \"DLLS=" + DllsFolder + "\"");
            s.AppendLine("set \"LOG=" + Path.Combine(UpdaterFolder, LogName) + "\"");
            s.AppendLine("set \"FLAG=" + Path.Combine(UpdaterFolder, MovedFlagName) + "\"");
            // TrimEnd: a path ending in \ right before a closing quote, e.g.
            // "C:\SB\", reads as an escaped quote (\") on a command line.
            s.AppendLine("set \"SBROOT=" + SbFolder.TrimEnd('\\') + "\"");
            if (fixScript != null)
                s.AppendLine("set \"FIXPS=" + fixScript + "\"");
            s.AppendLine("set \"SBEXE=" + SbRestart.SbExePath + "\"");          // NEW
            s.AppendLine("set \"RELAUNCH=" + SbRestart.RelaunchFlagPath + "\""); // NEW
            s.AppendLine("set \"RESULT=" + Path.Combine(UpdaterFolder, LastRunName) + "\"");  // NEW: ok / failed for the next start
            s.AppendLine();

            // Wait for Streamer.bot to exit. tasklist prints the process if it
            // is alive; find returns errorlevel 0 when it sees the PID.
            // "ping -n 3" is the classic batch way to sleep ~2 seconds.
            s.AppendLine(":wait");
            s.AppendLine("tasklist /FI \"PID eq %PID%\" /NH 2>nul | find \" %PID% \" >nul");
            s.AppendLine("if not errorlevel 1 (");
            s.AppendLine("  ping 127.0.0.1 -n 3 >nul");
            s.AppendLine("  goto wait");
            s.AppendLine(")");
            s.AppendLine("rem Give Windows a moment to release file handles.");
            s.AppendLine("ping 127.0.0.1 -n 2 >nul");
            s.AppendLine();
            s.AppendLine("if not exist \"%DLLS%\" mkdir \"%DLLS%\"");
            s.AppendLine();

            // a) A downloaded update wins: copy it straight into dlls\.
            s.AppendLine("if exist \"%PENDING%\" (");
            s.AppendLine("  copy /Y \"%PENDING%\" \"%TARGET%\" >nul");
            s.AppendLine("  if errorlevel 1 goto failed");
            s.AppendLine("  del /F /Q \"%PENDING%\"");
            s.AppendLine("  echo %DATE% %TIME% Installed update into \"%TARGET%\">>\"%LOG%\"");
            if (moveOld) s.AppendLine("  echo moved>\"%FLAG%\"");   // update + move in one go
            s.AppendLine("  goto cleanup");
            s.AppendLine(")");
            s.AppendLine();

            // b) No update: move the running DLL into dlls\ (only if it was outside).
            if (moveOld)
            {
                s.AppendLine("if exist \"%LOADED%\" (");
                s.AppendLine("  copy /Y \"%LOADED%\" \"%TARGET%\" >nul");
                s.AppendLine("  if errorlevel 1 goto failed");
                s.AppendLine("  echo %DATE% %TIME% Moved \"%LOADED%\" to \"%TARGET%\">>\"%LOG%\"");
                s.AppendLine("  echo moved>\"%FLAG%\"");
                s.AppendLine(")");
                s.AppendLine();
            }

            // NOTE: every path is wrapped in quotes, even in echo lines. A path
            //    like "C:\Program Files (x86)\..." has a ")" in it, and an
            //    unquoted ")" inside an if ( ... ) block ends the block early.

            // c) Only once dlls\OSWTools.dll definitely exists do we delete the
            //    old copy. Copy-then-delete (not move) means a failure halfway
            //    never leaves the user with NO DLL at all.
            s.AppendLine(":cleanup");
            s.AppendLine("if exist \"%TARGET%\" if exist \"%LEGACY%\" del /F /Q \"%LEGACY%\"");
            s.AppendLine("set \"OUTCOME=ok\"");
            // An old root copy that couldn't be deleted can still be the one
            // Streamer.bot loads, so that counts as a failure.
            s.AppendLine("if exist \"%LEGACY%\" set \"OUTCOME=failed\"");

            // d) Point saved action references at dlls\OSWTools.dll.
            //    -ExecutionPolicy Bypass applies to this one run only; it does
            //    not change the user's system setting.
            //    The script exits with 0 when everything worked. Anything else
            //    (including PowerShell being blocked or missing) is a failure.
            //    "if errorlevel 1" means "exit code 1 OR HIGHER".
            if (fixScript != null)
            {
                s.AppendLine("if exist \"%TARGET%\" (");
                s.AppendLine("  powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%FIXPS%\" -SbFolder \"%SBROOT%\" -LogFile \"%LOG%\"");
                s.AppendLine("  if errorlevel 1 set \"OUTCOME=failed\"");
                s.AppendLine(")");
            }

            // NEW: record the outcome for the DLL's next start.
            // ">file echo text" puts the redirect first, so text ending in a
            // digit can't be mistaken for a handle number (echo 1>file).
            s.AppendLine(">\"%RESULT%\" echo %OUTCOME%");
            s.AppendLine("goto done");
            s.AppendLine();
            s.AppendLine(":failed");
            s.AppendLine("echo %DATE% %TIME% FAILED to copy into \"%TARGET%\" - left files as they were>>\"%LOG%\"");
            s.AppendLine(">\"%RESULT%\" echo failed");
            s.AppendLine();
            s.AppendLine(":done");
            if (fixScript != null) s.AppendLine("del \"%FIXPS%\" >nul 2>&1");

            // e) NEW: reopen Streamer.bot if the user clicked "Restart".
            //    This runs even after a failed copy, so the user is never
            //    left without Streamer.bot.
            //    "endlocal & start ..." — cmd fills in %SBROOT% and %SBEXE%
            //    BEFORE running the line, so endlocal can clear this script's
            //    variables first and Streamer.bot starts with a clean
            //    environment. start "" = empty window title (start treats the
            //    first quoted text as a title). /D sets the working folder.
            s.AppendLine("if exist \"%RELAUNCH%\" (");
            s.AppendLine("  del /F /Q \"%RELAUNCH%\"");
            s.AppendLine("  echo %DATE% %TIME% Reopening Streamer.bot>>\"%LOG%\"");
            s.AppendLine("  endlocal & start \"\" /D \"%SBROOT%\" \"%SBEXE%\"");
            s.AppendLine(")");
            s.AppendLine("del \"%~f0\"");   // the script deletes itself
            return s.ToString();
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
                                     Path.GetFullPath(b).TrimEnd('\\'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
