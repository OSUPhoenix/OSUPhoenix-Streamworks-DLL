namespace OSWTools.Versioning
{
    /// <summary>Result of a compatibility check between a tool and the installed DLL.</summary>
    public class CompatibilityResult
    {
        public bool   IsCompatible     { get; set; }
        public string ToolName         { get; set; }
        public string RequiredVersion  { get; set; }
        public string InstalledVersion { get; set; }
        public string Message          { get; set; }
        public bool   IsBreakingChange { get; set; }
    }

    /// <summary>
    /// Compares semantic version strings to check whether the installed
    /// OSWTools DLL meets a tool's minimum version requirement.
    /// </summary>
    public static class CompatibilityChecker
    {
        /// <summary>
        /// Checks whether the current DLL satisfies the tool's minimum version.
        /// Example:
        ///   var result = CompatibilityChecker.Check("My Tool", "1.0.0");
        ///   if (!result.IsCompatible) ShowError(result.Message);
        /// </summary>
        public static CompatibilityResult Check(string toolName, string requiredVersion)
        {
            System.Version installed = ParseVersion(OSWVersion.Current);
            System.Version required  = ParseVersion(requiredVersion);

            if (installed == null || required == null)
            {
                return new CompatibilityResult
                {
                    IsCompatible     = false,
                    ToolName         = toolName,
                    RequiredVersion  = requiredVersion,
                    InstalledVersion = OSWVersion.Current,
                    Message          = "Could not parse version strings. Installed='" + OSWVersion.Current + "' Required='" + requiredVersion + "'",
                    IsBreakingChange = false
                };
            }

            bool compatible = installed >= required;
            bool breaking   = !compatible && installed.Major < required.Major;

            string msg;
            if (compatible)
                msg = toolName + " is compatible. (Installed: " + OSWVersion.Current + ", Required: >= " + requiredVersion + ")";
            else if (breaking)
                msg = toolName + " requires a MAJOR update to OSWTools. Please update from v" + OSWVersion.Current + " to at least v" + requiredVersion + ".";
            else
                msg = toolName + " needs a newer OSWTools. Please update from v" + OSWVersion.Current + " to at least v" + requiredVersion + ".";

            return new CompatibilityResult
            {
                IsCompatible     = compatible,
                ToolName         = toolName,
                RequiredVersion  = requiredVersion,
                InstalledVersion = OSWVersion.Current,
                Message          = msg,
                IsBreakingChange = breaking
            };
        }

        // Parses "Major.Minor.Patch" into a Version for comparison.
        private static System.Version ParseVersion(string v)
        {
            string clean = v.TrimStart('v', 'V').Trim();
            System.Version result;
            return System.Version.TryParse(clean, out result) ? result : null;
        }
    }
}
