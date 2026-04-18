// =============================================================================
// OSWTools — Versioning/IntegrationRegistry.cs
//
// Cross-tool integration registry.
//
// DIFFERENT FROM Versioning.cs:
//   Versioning.cs handles tool-to-DLL compatibility (does my tool need a newer
//   OSWTools DLL?). This file handles tool-to-tool dependencies (does the
//   Achievement System's CGGC integration hook have CGGC installed at an
//   acceptable version?).
//
// PROTOCOL (persisted globals, namespaced "OSUP_" to match existing conventions):
//   OSUP_<Code>_Installed  →  bool    (true if module is present)
//   OSUP_<Code>_Version    →  string  (semver, e.g. "1.2.3")
//
//   Every OSW tool writes these two globals on startup via DeclareInstalled().
//   The DLL itself self-registers as OSUP_OSW_* on first use.
//
// USAGE — tool declaring itself present:
//   Lib.DeclareInstalled("SAS", "3.3.0");   // usually in Execute()
//
// USAGE — tool declaring a dependency:
//   Lib.RegisterIntegration(new IntegrationRule
//   {
//       ModuleCode       = "CGGC",
//       ModuleName       = "Who's That Game Character",
//       MinVersion       = "1.0.0",
//       MaxTestedVersion = "1.0.0",
//       AllowNewer       = true,   // warn but proceed if CGGC > 1.0.0
//       UpgradeMessage   = "Update CGGC to v1.0.0+ for achievement integration."
//   });
//
// USAGE — checking at integration hook time:
//   if (!Lib.IsIntegrationAllowed("CGGC")) return;
//   // ...proceed with integration logic
//
// WARNINGS:
//   Failed checks log via Lib.LogWarn (every time) and toast via Lib.ShowToast
//   (deduped — only the FIRST time a given (ModuleCode, Reason) occurs per SB
//   session). This prevents toast spam on repeated integration hooks.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace OSWTools
{
    // ─────────────────────────────────────────────────────────────────────────
    // PUBLIC: A single rule describing a tool's dependency on another module.
    //
    // Fields mirror the SAS v3.0 Rule shape so existing configurations can be
    // passed to RegisterIntegration() unchanged.
    // ─────────────────────────────────────────────────────────────────────────
    public class IntegrationRule
    {
        /// <summary>Unique short code for the module, e.g. "CGGC". Case-insensitive.</summary>
        public string ModuleCode { get; set; }

        /// <summary>Human-readable name for warnings, e.g. "Who's That Game Character".</summary>
        public string ModuleName { get; set; }

        /// <summary>Minimum acceptable version (semver). Null/empty = no minimum.</summary>
        public string MinVersion { get; set; }

        /// <summary>
        /// Maximum tested-against version. Newer versions will warn unless
        /// AllowNewer = true. Null/empty = no upper bound.
        /// </summary>
        public string MaxTestedVersion { get; set; }

        /// <summary>If true, older-than-MinVersion is allowed (with warning).</summary>
        public bool AllowOlder { get; set; }

        /// <summary>If true, newer-than-MaxTestedVersion is allowed (with warning).</summary>
        public bool AllowNewer { get; set; }

        /// <summary>
        /// Optional back-reference: this module requires THIS TOOL to be at
        /// RequiresMinVersion or higher. Used for two-way compatibility checks.
        /// Pass the tool name that called RegisterIntegration().
        /// </summary>
        public string RequiresMinVersion { get; set; }

        /// <summary>Message shown when a check fails and the module is blocked.</summary>
        public string UpgradeMessage { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUBLIC: Result of an integration check. Callers usually use
    // IsIntegrationAllowed() for a bool answer; CheckIntegration() returns
    // this richer result for UIs that want to show the reason.
    // ─────────────────────────────────────────────────────────────────────────
    public class IntegrationCheckResult
    {
        public bool   Allowed          { get; set; }
        public bool   IsWarning        { get; set; } // allowed = true but logged a warning
        public string ModuleCode       { get; set; }
        public string ModuleName       { get; set; }
        public string InstalledVersion { get; set; }
        public string RequiredVersion  { get; set; }
        public string Reason           { get; set; } // human-readable explanation
    }

    // ═════════════════════════════════════════════════════════════════════════
    // OSWLib partial — integration registry methods
    // ═════════════════════════════════════════════════════════════════════════
    public partial class OSWLib
    {
        private const string RegistryPrefix = "OSUP_"; // matches existing tool convention
        private const string DllModuleCode  = "OSW";   // DLL self-registers as this

        // ── Per-session state ─────────────────────────────────────────────────
        //
        // Rules are stored PER OSWLib INSTANCE (i.e. per tool, by _ToolName) so
        // each tool manages its own set of dependency rules. A shared static
        // would have to key on tool name anyway, and this keeps things simple.
        //
        // Warning dedup is static so toast spam is suppressed across the
        // whole session regardless of which tool triggered the check.
        // ──────────────────────────────────────────────────────────────────────
        private readonly Dictionary<string, IntegrationRule> _integrationRules
            = new Dictionary<string, IntegrationRule>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> _shownWarnings
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _warningLock = new object();

        private static bool _dllSelfRegistered = false;
        private static readonly object _dllSelfRegLock = new object();

        // ═════════════════════════════════════════════════════════════════════
        // DECLARATION — mark a module as present
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Writes the OSUP_<Code>_Installed + OSUP_<Code>_Version globals so
        /// other tools can detect this module. Safe to call repeatedly — cheap
        /// no-op if the globals already match.
        ///
        /// Call once at the top of Execute() for your tool:
        ///   Lib.DeclareInstalled("SAS", "3.3.0");
        /// </summary>
        public void DeclareInstalled(string moduleCode, string version)
        {
            if (string.IsNullOrWhiteSpace(moduleCode)) return;
            if (version == null) version = "";

            try
            {
                _CPH.SetGlobalVar(RegistryPrefix + moduleCode + "_Installed", true, true);

                // Only write version if it's changed — avoids unnecessary disk writes
                string existing = _CPH.GetGlobalVar<string>(
                    RegistryPrefix + moduleCode + "_Version", true) ?? "";
                if (!existing.Equals(version, StringComparison.OrdinalIgnoreCase))
                    _CPH.SetGlobalVar(RegistryPrefix + moduleCode + "_Version", version, true);
            }
            catch (Exception ex)
            {
                LogWarn("[Integration] DeclareInstalled(" + moduleCode + ") failed: " + ex.Message);
            }
        }

        // ── Lazy DLL self-registration ────────────────────────────────────────
        //
        // Called automatically from any public Integration* method. Writes
        // OSUP_OSW_Installed + OSUP_OSW_Version so any tool using the registry
        // can detect the DLL the same way it detects other tools.
        //
        // Guarded with double-check locking so it's effectively zero-cost after
        // the first call in the session.
        // ──────────────────────────────────────────────────────────────────────
        private void EnsureDllSelfRegistered()
        {
            if (_dllSelfRegistered) return;
            lock (_dllSelfRegLock)
            {
                if (_dllSelfRegistered) return;
                try
                {
                    _CPH.SetGlobalVar(RegistryPrefix + DllModuleCode + "_Installed", true, true);
                    _CPH.SetGlobalVar(RegistryPrefix + DllModuleCode + "_Version",
                        OSWVersion.Current, true);
                }
                catch (Exception ex)
                {
                    LogWarn("[Integration] DLL self-registration failed: " + ex.Message);
                }
                // Set the flag even on failure so we don't retry every call
                _dllSelfRegistered = true;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // RULES — register dependencies
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Register a rule describing a module this tool depends on. Usually
        /// called once per rule at Execute() time. Re-registering the same
        /// ModuleCode replaces the previous rule.
        /// </summary>
        public void RegisterIntegration(IntegrationRule rule)
        {
            EnsureDllSelfRegistered();
            if (rule == null || string.IsNullOrWhiteSpace(rule.ModuleCode)) return;
            _integrationRules[rule.ModuleCode] = rule;
        }

        /// <summary>Register multiple rules in one call. Convenience for array-driven configs.</summary>
        public void RegisterIntegrations(params IntegrationRule[] rules)
        {
            if (rules == null) return;
            foreach (var r in rules) RegisterIntegration(r);
        }

        // ═════════════════════════════════════════════════════════════════════
        // QUERIES — direct lookups on the global registry (no rule needed)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>True if OSUP_<Code>_Installed is set. Safe if the var doesn't exist.</summary>
        public bool IsModuleInstalled(string moduleCode)
        {
            EnsureDllSelfRegistered();
            if (string.IsNullOrWhiteSpace(moduleCode)) return false;
            try
            {
                return _CPH.GetGlobalVar<bool>(RegistryPrefix + moduleCode + "_Installed", true);
            }
            catch { return false; }
        }

        /// <summary>Reads OSUP_<Code>_Version, or "" if absent/unreadable.</summary>
        public string GetModuleVersion(string moduleCode)
        {
            EnsureDllSelfRegistered();
            if (string.IsNullOrWhiteSpace(moduleCode)) return "";
            try
            {
                return _CPH.GetGlobalVar<string>(RegistryPrefix + moduleCode + "_Version", true) ?? "";
            }
            catch { return ""; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // CHECKS — evaluate a registered rule at integration-hook time
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Quick bool check: should this integration proceed? False means
        /// either the module isn't installed, or its version is out of range
        /// and the rule doesn't allow it.
        /// </summary>
        public bool IsIntegrationAllowed(string moduleCode)
        {
            return CheckIntegration(moduleCode).Allowed;
        }

        /// <summary>
        /// Full check result — returns Allowed, Reason, version info. Always
        /// returns a non-null result; treat unknown ModuleCode as Allowed=true
        /// (no rule means no constraint).
        /// </summary>
        public IntegrationCheckResult CheckIntegration(string moduleCode)
        {
            EnsureDllSelfRegistered();

            var result = new IntegrationCheckResult
            {
                ModuleCode = moduleCode,
                Allowed    = true
            };

            if (string.IsNullOrWhiteSpace(moduleCode))
                return result;

            // No rule registered → no constraint, allow freely
            IntegrationRule rule;
            if (!_integrationRules.TryGetValue(moduleCode, out rule))
                return result;

            result.ModuleName      = rule.ModuleName;
            result.RequiredVersion = rule.MinVersion;

            // ── Back-reference check: does the DEPENDENT tool meet the
            // RequiresMinVersion bar? (e.g. CGGC rule says "I need SAS >= 3.0")
            if (!string.IsNullOrWhiteSpace(rule.RequiresMinVersion))
            {
                string selfVer = GetModuleVersion(_ToolName); // uses OSUP_<ToolName>_Version
                if (CompareVersionsSafe(selfVer, rule.RequiresMinVersion) < 0)
                {
                    return FailCheck(result, rule, null,
                        _ToolName + " v" + selfVer + " < required v" + rule.RequiresMinVersion
                        + " for " + (rule.ModuleName ?? moduleCode) + ".");
                }
            }

            // ── Module presence
            if (!IsModuleInstalled(moduleCode))
            {
                // Not installed is a hard block — no warning, just silent skip.
                // (Tools frequently check optional integrations; spamming "X
                //  is not installed" toasts every chat message isn't useful.)
                result.Allowed = false;
                result.Reason  = (rule.ModuleName ?? moduleCode) + " is not installed.";
                return result;
            }

            string installedVer = GetModuleVersion(moduleCode);
            result.InstalledVersion = installedVer;

            // If installed but unversioned, allow with a one-shot warning
            if (string.IsNullOrWhiteSpace(installedVer))
            {
                EmitWarning(moduleCode, "unversioned",
                    (rule.ModuleName ?? moduleCode) + " is installed but has no version.");
                result.IsWarning = true;
                result.Reason    = "Installed but unversioned.";
                return result;
            }

            // ── Min version check
            if (!string.IsNullOrWhiteSpace(rule.MinVersion)
                && CompareVersionsSafe(installedVer, rule.MinVersion) < 0)
            {
                if (rule.AllowOlder)
                {
                    EmitWarning(moduleCode, "older",
                        (rule.ModuleName ?? moduleCode) + " v" + installedVer
                        + " is older than min v" + rule.MinVersion + ".");
                    result.IsWarning = true;
                    result.Reason    = "Older than MinVersion but AllowOlder = true.";
                    return result;
                }
                return FailCheck(result, rule, installedVer,
                    (rule.ModuleName ?? moduleCode) + " v" + installedVer
                    + " is too old. " + (rule.UpgradeMessage ?? ""));
            }

            // ── Max tested version check
            if (!string.IsNullOrWhiteSpace(rule.MaxTestedVersion)
                && CompareVersionsSafe(installedVer, rule.MaxTestedVersion) > 0)
            {
                if (rule.AllowNewer)
                {
                    EmitWarning(moduleCode, "newer",
                        (rule.ModuleName ?? moduleCode) + " v" + installedVer
                        + " is newer than tested v" + rule.MaxTestedVersion + ".");
                    result.IsWarning = true;
                    result.Reason    = "Newer than MaxTestedVersion but AllowNewer = true.";
                    return result;
                }
                return FailCheck(result, rule, installedVer,
                    (rule.ModuleName ?? moduleCode) + " v" + installedVer
                    + " > tested v" + rule.MaxTestedVersion
                    + " and not marked forward-compatible.");
            }

            // All checks passed
            return result;
        }

        /// <summary>
        /// Runs CheckIntegration on every registered rule and returns the
        /// results. Useful in settings UIs for showing a "dependencies OK"
        /// panel, or to force warning toasts to surface on startup.
        /// </summary>
        public List<IntegrationCheckResult> CheckAllIntegrations()
        {
            EnsureDllSelfRegistered();
            var results = new List<IntegrationCheckResult>();
            foreach (var code in _integrationRules.Keys.ToList())
                results.Add(CheckIntegration(code));
            return results;
        }

        // ═════════════════════════════════════════════════════════════════════
        // INTERNAL — warnings, version comparison
        // ═════════════════════════════════════════════════════════════════════

        private IntegrationCheckResult FailCheck(
            IntegrationCheckResult r, IntegrationRule rule, string installedVer, string reason)
        {
            r.Allowed          = false;
            r.InstalledVersion = installedVer;
            r.Reason           = reason;
            EmitWarning(rule.ModuleCode, "blocked", reason);
            return r;
        }

        // Logs every time; toasts only the first time (moduleCode, reasonKey)
        // appears this session. Prevents spam when an integration hook fires
        // on every chat message.
        private void EmitWarning(string moduleCode, string reasonKey, string message)
        {
            string dedupKey = (moduleCode ?? "") + "|" + (reasonKey ?? "");

            bool shouldToast;
            lock (_warningLock)
            {
                shouldToast = _shownWarnings.Add(dedupKey);
            }

            LogWarn("[Integration:" + moduleCode + "] " + message);
            if (shouldToast)
                ShowToast("OSW Integration (" + moduleCode + ")", message);
        }

        // Uses System.Version — handles "1.2.3" and "1.2.3.4" formats.
        // Anything un-parseable (empty, "v1.0-beta") falls back to 0.0.0.0
        // which sorts below all real versions.
        private static int CompareVersionsSafe(string left, string right)
        {
            Version lv = ParseVersionSafe(left);
            Version rv = ParseVersionSafe(right);
            return lv.CompareTo(rv);
        }

        private static Version ParseVersionSafe(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return new Version(0, 0, 0, 0);
            // Strip "v" prefix and pre-release suffix ("-beta", "+build")
            string core = v.TrimStart('v', 'V').Trim();
            int dash = core.IndexOf('-');
            int plus = core.IndexOf('+');
            int cut  = dash >= 0 && (plus < 0 || dash < plus) ? dash : plus;
            if (cut >= 0) core = core.Substring(0, cut);

            Version result;
            return Version.TryParse(core, out result) ? result : new Version(0, 0, 0, 0);
        }
    }
}
