using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

        // ── Registration ──────────────────────────────────────────────────────────

        /// <summary>
        /// Registers this tool with the DLL and checks version compatibility.
        /// Call once at the top of Execute().
        ///
        /// USAGE:
        ///   var lib = new OSWLib(CPH, "Achievement System");
        ///   var reg = lib.Register("3.0.0", minDllVersion: "2.0.0");
        ///   if (!reg.IsCompatible) CPH.LogWarn(reg.Message);
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
                LogDebug("Registered v" + toolVersion + " — DLL v" + OSWVersion.Current + " OK.");
            else
                LogWarn(result.Message);

            return result;
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
