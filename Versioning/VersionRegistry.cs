using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OSWTools.Versioning
{
    /// <summary>A tool that has registered itself with the OSWTools DLL.</summary>
    public class ToolRegistration
    {
        public string   ToolName      { get; set; }
        public string   ToolVersion   { get; set; }
        public string   MinDllVersion { get; set; }
        public DateTime RegisteredAt  { get; set; }
    }

    /// <summary>
    /// Central log of every OSW tool that has registered with the DLL.
    /// Call Register() once at startup in each tool.
    ///
    /// Usage:
    ///   var result = VersionRegistry.Register("Achievement System", "3.2.1", "1.0.0");
    ///   if (!result.IsCompatible) ShowWarning(result.Message);
    /// </summary>
    public static class VersionRegistry
    {
        private static readonly Dictionary<string, ToolRegistration> _registry
            = new Dictionary<string, ToolRegistration>();

        private static readonly object _lock = new object();

        /// <summary>
        /// Registers a tool and immediately returns a compatibility result.
        /// </summary>
        public static CompatibilityResult Register(string toolName, string toolVersion, string minDllVersion = "1.0.0")
        {
            ToolRegistration reg = new ToolRegistration
            {
                ToolName      = toolName,
                ToolVersion   = toolVersion,
                MinDllVersion = minDllVersion,
                RegisteredAt  = DateTime.Now
            };

            lock (_lock) { _registry[toolName] = reg; }

            return CompatibilityChecker.Check(toolName, minDllVersion);
        }

        /// <summary>Returns all currently registered tools.</summary>
        public static List<ToolRegistration> GetAll()
        {
            lock (_lock) { return new List<ToolRegistration>(_registry.Values); }
        }

        /// <summary>Returns the registration for a specific tool, or null if not found.</summary>
        public static ToolRegistration Get(string toolName)
        {
            lock (_lock)
            {
                ToolRegistration reg;
                return _registry.TryGetValue(toolName, out reg) ? reg : null;
            }
        }

        /// <summary>Returns true if the named tool is registered.</summary>
        public static bool IsRegistered(string toolName)
        {
            lock (_lock) { return _registry.ContainsKey(toolName); }
        }

        /// <summary>
        /// Returns a formatted diagnostic report of all registered tools.
        /// Useful for an About or Diagnostics window.
        /// </summary>
        public static string GetDiagnosticsReport()
        {
            lock (_lock)
            {
                if (_registry.Count == 0) return "No tools registered.";

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("OSWTools v" + OSWVersion.Current + " — Registered Tools");
                sb.AppendLine(new string('-', 50));

                List<ToolRegistration> sorted = _registry.Values.OrderBy(r => r.ToolName).ToList();
                foreach (ToolRegistration r in sorted)
                {
                    CompatibilityResult check = CompatibilityChecker.Check(r.ToolName, r.MinDllVersion);
                    string status = check.IsCompatible ? "OK" : "INCOMPATIBLE";
                    sb.AppendLine("  " + r.ToolName + " v" + r.ToolVersion + "  [" + status + "]");
                    sb.AppendLine("    Requires DLL >= " + r.MinDllVersion + "  |  Registered: " + r.RegisteredAt.ToString("HH:mm:ss"));
                    if (!check.IsCompatible)
                        sb.AppendLine("    ! " + check.Message);
                }

                return sb.ToString();
            }
        }
    }
}
