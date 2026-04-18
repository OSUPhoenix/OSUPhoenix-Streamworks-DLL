// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Versioning/UpdateManager.cs
//
//  Adds CheckForUpdates() to the OSWLib partial class.
//  This is the ONE method that tool scripts call to trigger the
//  full update flow: check GitHub → prompt user → download → stage.
//
//  PLACEMENT:
//    Versioning/ → partial OSWLib, CPH-dependent.
//    No csproj change needed — the existing wildcard glob picks it up.
//
//  USAGE (call once at the top of a tool's Execute()):
//
//    var lib = new OSWLib(CPH, "Achievement System");
//    lib.Register("3.0.0", minDllVersion: "1.0.0");
//    lib.CheckForUpdates();   // ← one-liner, does everything
//
//  WHAT HAPPENS:
//    1. Checks a session flag — if we already checked this SB session,
//       returns immediately (no spamming the user).
//    2. Checks a persisted global — if the user previously skipped THIS
//       specific version, returns immediately (respects their choice).
//    3. Calls UpdateChecker.CheckAsync() to hit the GitHub API.
//    4. If an update is available, opens a themed WinForms dialog
//       (UpdatePromptForm) on an STA thread.
//    5. If the user clicks "Download & Install", downloads the DLL,
//       stages the swap script, and tells them to restart SB.
//    6. If the user clicks "Skip", stores the skipped version in a
//       persisted global so they won't be prompted again for that
//       version (they WILL be prompted for the next release).
//
//  THROTTLING:
//    - Once per SB session (static flag _updateCheckedThisSession)
//    - Skipped versions stored in global "osw_SkippedUpdateVersion"
//    - Network errors are logged but never block the tool from running
// ═══════════════════════════════════════════════════════════════════

using System;
using OSWTools.Utilities;
using OSWTools.Versioning;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ── Static throttle ───────────────────────────────────────────────
        // Only check once per Streamer.bot session, regardless of how many
        // tools call CheckForUpdates(). Resets when SB restarts (because
        // static fields are cleared when the DLL is reloaded).
        private static bool _updateCheckedThisSession = false;
        private static readonly object _updateCheckLock = new object();

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Checks GitHub for a new version of OSWTools.dll and prompts the
        /// user to update if one is available.
        ///
        /// Safe to call from every tool at startup — it only runs the actual
        /// check once per SB session. Subsequent calls are instant no-ops.
        ///
        /// Never throws. Network errors and dialog failures are logged
        /// as warnings but never prevent the calling tool from running.
        ///
        /// USAGE:
        ///   var lib = new OSWLib(CPH, "Achievement System");
        ///   lib.CheckForUpdates();
        /// </summary>
        public void CheckForUpdates()
        {
            // ── Fast path: already checked this session ───────────────────
            if (_updateCheckedThisSession) return;

            lock (_updateCheckLock)
            {
                // Double-check inside lock (another thread may have just finished)
                if (_updateCheckedThisSession) return;
                _updateCheckedThisSession = true;
            }

            // Run the actual check + prompt outside the lock so we don't
            // hold it while waiting on network or the user clicking a button.
            try
            {
                RunUpdateCheck();
            }
            catch (Exception ex)
            {
                // Never let the update check crash the calling tool
                LogWarn("Update check failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Forces an update check regardless of the session throttle.
        /// Useful for a manual "Check for Updates" button in a settings form.
        ///
        /// Unlike CheckForUpdates(), this will always hit the GitHub API
        /// and show the dialog if an update is available, even if a check
        /// was already done this session.
        /// </summary>
        public void ForceCheckForUpdates()
        {
            try
            {
                RunUpdateCheck();
            }
            catch (Exception ex)
            {
                LogWarn("Force update check failed: " + ex.Message);
            }
        }

        // ── Internal: the actual check + prompt flow ──────────────────────

        private void RunUpdateCheck()
        {
            // ── Step 1: Hit GitHub API ────────────────────────────────────
            LogDebug("Checking for OSWTools updates...");

            UpdateCheckResult result;
            try
            {
                result = UpdateChecker.CheckAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogWarn("Could not reach GitHub for update check: " + ex.Message);
                return;
            }

            if (!result.CheckSucceeded)
            {
                LogWarn("Update check did not succeed: " + result.ErrorMessage);
                return;
            }

            if (!result.UpdateAvailable)
            {
                LogDebug("OSWTools is up to date (v" + result.InstalledVersion + ").");
                return;
            }

            // ── Step 2: Check if user previously skipped this version ─────
            string skippedVersion = GetGlobal<string>("osw_SkippedUpdateVersion", "");
            if (string.Equals(skippedVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
            {
                LogDebug("Update v" + result.LatestVersion
                         + " available but user previously skipped it.");
                return;
            }

            // ── Step 3: Check if a download is already staged ─────────────
            if (UpdateChecker.IsPendingUpdatePresent())
            {
                LogInfo("OSWTools v" + result.LatestVersion
                        + " is already downloaded — restart Streamer.bot to apply.");
                ShowToast("OSWTools Update Ready",
                    "v" + result.LatestVersion + " is downloaded. Restart Streamer.bot to apply.");
                return;
            }

            // ── Step 4: Show the update prompt dialog ─────────────────────
            LogInfo("OSWTools update available: v" + result.InstalledVersion
                    + " → v" + result.LatestVersion);

            bool updateWasStaged = false;
            bool userSkipped     = false;

            DpiHelper.EnsureDpiAware();
            StaThread.Run(() =>
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

                using (var form = new UpdatePromptForm(result))
                {
                    var dialogResult = form.ShowDialog();

                    if (form.UpdateStaged)
                    {
                        updateWasStaged = true;
                    }
                    else if (dialogResult == System.Windows.Forms.DialogResult.Cancel)
                    {
                        userSkipped = true;
                    }
                }
            });

            // ── Step 5: Handle the result ─────────────────────────────────
            if (updateWasStaged)
            {
                LogInfo("OSWTools v" + result.LatestVersion
                        + " downloaded and staged. Restart Streamer.bot to apply.");
                ShowToast("OSWTools Updated",
                    "v" + result.LatestVersion + " is ready. Restart Streamer.bot to apply.");
            }
            else if (userSkipped)
            {
                // Store the skipped version so we don't prompt again for it.
                // They WILL be prompted for the next release after this one.
                SetGlobal("osw_SkippedUpdateVersion", result.LatestVersion);
                LogInfo("User skipped OSWTools update v" + result.LatestVersion + ".");
            }
        }
    }
}
