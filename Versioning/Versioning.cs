using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using OSWTools.Versioning;

namespace OSWTools
{
    /// <summary>
    /// Stores a record of a tool that has registered with OSWLib this session.
    /// </summary>
    public class ToolRegistration
    {
        public string   ToolName      { get; set; }
        public string   ToolVersion   { get; set; }
        public string   MinDllVersion { get; set; }
        public DateTime RegisteredAt  { get; set; }
    }

    /// <summary>
    /// The result of a compatibility check between a tool and the installed DLL.
    /// </summary>
    public class CompatibilityResult
    {
        public bool   IsCompatible     { get; set; }
        public bool   IsBreakingChange { get; set; }
        public string ToolName         { get; set; }
        public string RequiredVersion  { get; set; }
        public string InstalledVersion { get; set; }
        public string Message          { get; set; }
    }

    public partial class OSWLib
    {
        // ── Registry (static — shared across all OSWLib instances) ────────────────
        private static readonly Dictionary<string, ToolRegistration> _registry
            = new Dictionary<string, ToolRegistration>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _registryLock = new object();

        // ── Incompatibility dialog dedup (★ added for the per-tool dialog) ───────
        //
        // PURPOSE
        //   Track which tools have already shown the "update required" dialog
        //   this SB session so we don't pop the same window five times when
        //   five chat messages trigger the same handler.
        //
        // SCOPE
        //   Static — i.e. shared across every OSWLib instance for the entire
        //   SB session. Restarting SB clears it (because the static field
        //   gets re-initialised when the DLL reloads).
        //
        // KEY
        //   Tool name (case-insensitive). Matches the per-tool dedup pattern
        //   used by IntegrationRegistry.cs's _shownWarnings HashSet.
        // ─────────────────────────────────────────────────────────────────────────
        private static readonly HashSet<string> _shownIncompatibilityDialogs
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _dialogDedupLock = new object();

        // ── Master-list takeover switch (★ future-proof for the consolidated sweep)
        //
        // The long-term plan is a single master-sheet sweep at SB startup that
        // surfaces every out-of-date OSW product in ONE consolidated dialog.
        // When that lands, the master sweep should set this flag to true at
        // startup, and the per-tool Register() dialog will silently stand
        // down. No edits to Register() needed at that point — it just checks
        // this flag and bails out of the dialog code path.
        //
        // Defaults to false so the interim per-tool dialog is active today.
        // ─────────────────────────────────────────────────────────────────────────
        public static bool MasterUpdateCheckActive { get; set; } = false;

        // ── Registration ──────────────────────────────────────────────────────────

        /// <summary>
        /// Registers this tool with the DLL and checks version compatibility.
        /// Call once at the top of Execute().
        ///
        /// USAGE:
        ///   var lib = new OSWLib(CPH, "Achievement System");
        ///   var reg = lib.Register("3.0.0", minDllVersion: "2.0.0");
        ///   if (!reg.IsCompatible) CPH.LogWarn(reg.Message);
        ///
        /// SIDE EFFECT (★ added):
        ///   When the compatibility check fails AND MasterUpdateCheckActive
        ///   is false AND this tool hasn't already shown the dialog this
        ///   session, a modal-style "update required" dialog pops in front
        ///   of the user. The dialog is fire-and-forget on a separate STA
        ///   thread, so this method still returns immediately.
        /// </summary>
        public CompatibilityResult Register(string toolVersion, string minDllVersion = "1.0.0")
        {
            lock (_registryLock)
            {
                _registry[_ToolName] = new ToolRegistration
                {
                    ToolName      = _ToolName,
                    ToolVersion   = toolVersion,
                    MinDllVersion = minDllVersion,
                    RegisteredAt  = DateTime.Now
                };
            }

            CompatibilityResult result = CheckCompatibility(minDllVersion);

            if (result.IsCompatible)
            {
                LogDebug("Registered v" + toolVersion + " — DLL v" + OSWVersion.Current + " OK.");
            }
            else
            {
                LogWarn(result.Message);

                // ★ Surface the warning visually if the per-tool dialog system
                //   is in charge (i.e. the master sweep hasn't claimed
                //   responsibility yet). The helper handles dedup internally
                //   so callers don't have to.
                if (!MasterUpdateCheckActive)
                    ShowIncompatibilityDialogAsync(result);
            }

            return result;
        }

        // ── Incompatibility dialog launcher (★ added) ────────────────────────────
        //
        // Fires up the UpdateRequiredForm on a dedicated STA thread and does
        // NOT join — i.e. the calling thread continues immediately. This is
        // intentional: Register() lives at the top of every tool's Execute(),
        // and blocking Execute() until the user clicks OK would freeze the
        // tool on busy streams.
        //
        // Three guards before we actually show anything:
        //   1. MasterUpdateCheckActive must be false (master sweep not in charge)
        //   2. Tool must not have already shown the dialog this session (dedup)
        //   3. UpdateRequiredForm construction must not throw (defensive — if
        //      WinForms is unavailable in some weird headless context, we
        //      degrade silently rather than spamming exceptions)
        // ─────────────────────────────────────────────────────────────────────────
        private void ShowIncompatibilityDialogAsync(CompatibilityResult result)
        {
            // Guard #1: master sweep takeover — checked again here in case
            // the caller didn't (belt-and-braces).
            if (MasterUpdateCheckActive) return;

            // Guard #2: dedup. We claim the slot atomically so two near-
            // simultaneous Register() calls can't both pop a dialog.
            bool firstTime;
            lock (_dialogDedupLock)
            {
                firstTime = _shownIncompatibilityDialogs.Add(_ToolName);
            }
            if (!firstTime) return;

            // Build the GitHub releases URL from the existing OSWVersion
            // constants. Using the constants (rather than hardcoding) means
            // the URL auto-corrects once any typo in OSWVersion is fixed.
            // The /releases/latest path redirects browsers to the latest
            // release's HTML page — which is what we want for a human-facing
            // link (not the .dll download URL, which would just trigger a
            // file download).
            string releasesUrl = "https://github.com/" + OSWVersion.GitHubOwner
                               + "/" + OSWVersion.GitHubRepo + "/releases/latest";

            // Capture locals for the closure — instance fields like _ToolName
            // could theoretically change between thread start and dialog show
            // if the OSWLib instance were reused weirdly. Locals are safer.
            string toolName   = _ToolName;
            string installed  = result.InstalledVersion;
            string required   = result.RequiredVersion;
            bool   breaking   = result.IsBreakingChange;

            // Spin up an STA thread for the dialog. We don't Join() — the
            // dialog lives on its own thread, the calling Execute() continues.
            var thread = new Thread(() =>
            {
                try
                {
                    using (var form = new UpdateRequiredForm(
                        toolName, installed, required, breaking, releasesUrl))
                    {
                        form.ShowDialog();
                    }
                }
                catch (Exception ex)
                {
                    // We can't call LogWarn from the worker thread reliably
                    // (no CPH context guarantee), so fall back to a static
                    // file-log via OSWLogger if available. If that also
                    // fails, swallow — better to lose one log line than
                    // crash the user's stream.
                    try
                    {
                        OSWLogger.Warn(toolName,
                            "UpdateRequiredForm failed to display: " + ex.Message);
                    }
                    catch { /* nothing more we can do here */ }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;     // don't keep the process alive if SB exits
            thread.Start();
            // No Join — fire-and-forget. The dialog stays open until the
            // user clicks OK; this method has already returned.
        }

        // ─────────────────────────────────────────────────────────────────────
        // ★ MASTER UPDATE CHECK (Phase 2 — May 2026)
        //
        // Public entry point for the SB-startup-driven consolidated update
        // check. Wire this into a SB action triggered by "Application Started"
        // and the master sheet will be checked once per session.
        //
        // Behavior:
        //   1. Sets MasterUpdateCheckActive = true BEFORE the check runs, so
        //      per-tool incompatibility dialogs are silenced for the rest of
        //      the session — even if this check itself fails. The user gets
        //      ONE notification flow, not two.
        //   2. Dedups per session (HashSet check, like the per-tool dialog).
        //   3. Calls ProductRegistry.CheckAllInstalledProducts (which talks
        //      to the master sheet via ProductRegistryClient).
        //   4. If nothing's outdated: stays silent. No dialog, no toast.
        //      Per spec: success IS silence.
        //   5. If something's outdated: pops the consolidated dialog on a
        //      fire-and-forget STA thread so this method returns immediately
        //      (important for SB startup — we don't block the launch).
        //   6. If the check fails (manifest unreachable, etc): logs but
        //      stays visually silent. The user can manually re-run via
        //      whatever UI you wire this up to. Failure isn't a notification.
        // ─────────────────────────────────────────────────────────────────────

        // Dedup so callers (or future callers) can't accidentally double-pop.
        private static readonly object _masterCheckLock = new object();
        private static bool _masterCheckHasRun = false;

        /// <summary>
        /// Runs the master update check against the product registry sheet.
        /// Pops the consolidated dialog if updates are available, stays
        /// silent otherwise. Idempotent — only runs once per SB session.
        /// </summary>
        public void RunMasterUpdateCheck()
        {
            // ── Guard #1: master-list takeover — set FIRST, per spec ────
            // Even if this check fails, the per-tool fallback should stay
            // silent. Setting the flag before any other work guarantees
            // that — if we crash partway through, the rest of the session
            // is still in "master mode."
            MasterUpdateCheckActive = true;

            // ── Guard #2: dedup ─────────────────────────────────────────
            bool firstTime;
            lock (_masterCheckLock)
            {
                firstTime = !_masterCheckHasRun;
                _masterCheckHasRun = true;
            }
            if (!firstTime)
            {
                LogDebug("[Master] Skipped — already ran this session.");
                return;
            }

            // ── Run the check ───────────────────────────────────────────
            // CheckAllInstalledProducts is the synchronous bulk-check
            // method on the partial class (defined in ProductRegistry.cs).
            // Returns null on manifest failure, empty list on "all current",
            // non-empty list when there are updates.
            List<OSWLib.OutdatedProduct> outdated;
            try
            {
                outdated = CheckAllInstalledProducts();
            }
            catch (Exception ex)
            {
                // Defensive — CheckAllInstalledProducts shouldn't throw,
                // but a sheet-side surprise (HTML/CSV change at Google's
                // end, etc.) could bubble up unexpectedly.
                LogWarn("[Master] Update check threw: " + ex.Message);
                return;
            }

            if (outdated == null)
            {
                LogWarn("[Master] Update check skipped — manifest could not be fetched.");
                return;
            }

            if (outdated.Count == 0)
            {
                LogInfo("[Master] All installed products are up to date.");
                return;  // silent — no dialog
            }

            // ── Show the consolidated dialog ────────────────────────────
            LogInfo("[Master] " + outdated.Count + " product(s) outdated — showing update dialog.");
            ShowMasterUpdateDialogAsync(outdated);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ★ Master update dialog launcher (Phase 2 — May 2026)
        //
        // Mirrors ShowIncompatibilityDialogAsync's pattern exactly: fire-and-
        // forget on a background STA thread so the calling code (typically a
        // startup SB action) returns immediately.
        //
        // We pass the outdated list as a captured local — safer than reading
        // an instance field from the worker thread, since the caller's
        // OSWLib instance could in theory be reused before the dialog
        // finishes rendering.
        // ─────────────────────────────────────────────────────────────────────
        private void ShowMasterUpdateDialogAsync(List<OSWLib.OutdatedProduct> outdated)
        {
            if (outdated == null || outdated.Count == 0) return;

            // Capture for the closure.
            List<OSWLib.OutdatedProduct> localList = outdated;

            var thread = new Thread(() =>
            {
                try
                {
                    using (var form = new MasterUpdateForm(localList))
                    {
                        form.ShowDialog();
                    }
                }
                catch (Exception ex)
                {
                    // No CPH context from this thread — fall back to the
                    // static file logger. If that also fails, swallow:
                    // a missing log line is better than a stream crash.
                    try
                    {
                        OSWLogger.Warn("Master",
                            "MasterUpdateForm failed to display: " + ex.Message);
                    }
                    catch { /* nothing more we can do here */ }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        // ── Compatibility ─────────────────────────────────────────────────────────

        /// <summary>
        /// Checks whether the installed DLL meets a minimum version requirement.
        /// </summary>
        public CompatibilityResult CheckCompatibility(string minDllVersion)
        {
            Version installed = ParseVersion(OSWVersion.Current);
            Version required  = ParseVersion(minDllVersion);

            if (installed == null || required == null)
            {
                return new CompatibilityResult
                {
                    IsCompatible     = false,
                    IsBreakingChange = false,
                    ToolName         = _ToolName,
                    RequiredVersion  = minDllVersion,
                    InstalledVersion = OSWVersion.Current,
                    Message          = "Could not parse version strings. Installed='"
                                       + OSWVersion.Current + "' Required='" + minDllVersion + "'"
                };
            }

            bool compatible = installed >= required;
            bool breaking   = !compatible && installed.Major < required.Major;

            string msg = compatible
                ? _ToolName + " OK (DLL v" + OSWVersion.Current + " >= required v" + minDllVersion + ")"
                : breaking
                    ? _ToolName + " requires a MAJOR OSWTools update: installed v"
                      + OSWVersion.Current + ", needs v" + minDllVersion + " or higher."
                    : _ToolName + " needs a newer OSWTools: installed v"
                      + OSWVersion.Current + ", needs v" + minDllVersion + " or higher.";

            return new CompatibilityResult
            {
                IsCompatible     = compatible,
                IsBreakingChange = breaking,
                ToolName         = _ToolName,
                RequiredVersion  = minDllVersion,
                InstalledVersion = OSWVersion.Current,
                Message          = msg
            };
        }

        // ── Diagnostics ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a formatted report of all tools registered this session.
        /// Useful in an About / Diagnostics window.
        /// </summary>
        public static string GetDiagnosticsReport()
        {
            lock (_registryLock)
            {
                if (_registry.Count == 0) return "No tools registered this session.";

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("OSWTools v" + OSWVersion.Current + " — Registered Tools");
                sb.AppendLine(new string('-', 48));

                foreach (ToolRegistration r in _registry.Values.OrderBy(x => x.ToolName))
                {
                    Version inst = ParseVersion(OSWVersion.Current);
                    Version req  = ParseVersion(r.MinDllVersion);
                    bool ok = inst != null && req != null && inst >= req;
                    sb.AppendLine("  " + r.ToolName + " v" + r.ToolVersion
                                  + "  [" + (ok ? "OK" : "INCOMPATIBLE") + "]");
                    sb.AppendLine("    Requires DLL >= " + r.MinDllVersion
                                  + "  |  Registered: " + r.RegisteredAt.ToString("HH:mm:ss"));
                }

                return sb.ToString();
            }
        }

        /// <summary>Returns true if the named tool has registered this session.</summary>
        public static bool IsRegistered(string toolName)
        {
            lock (_registryLock) { return _registry.ContainsKey(toolName); }
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        private static Version ParseVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            Version result;
            return Version.TryParse(v.TrimStart('v', 'V').Trim(), out result) ? result : null;
        }
    }
}
