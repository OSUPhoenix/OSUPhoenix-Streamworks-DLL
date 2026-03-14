namespace OSWTools
{
    /// <summary>
    /// Logging helpers — all messages are prefixed with the tool name
    /// so the Streamer.bot log is readable when multiple tools are active.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "Achievement System");
    ///   lib.LogInfo("Starting up");        // → [Achievement System] Starting up
    ///   lib.LogWarn("Missing setting");    // → [Achievement System] Missing setting
    ///   lib.LogError("Save failed");       // → [Achievement System] Save failed
    ///   lib.LogDebug("Raw value: 42");     // → only appears when osw_DebugMode = true
    /// </summary>
    public partial class OSWLib
    {
        /// <summary>Writes an informational message to the Streamer.bot log.</summary>
        public void LogInfo(string message)
        {
            _CPH.LogInfo(Prefix(message));
        }

        /// <summary>Writes a warning message to the Streamer.bot log.</summary>
        public void LogWarn(string message)
        {
            _CPH.LogWarn(Prefix(message));
        }

        /// <summary>Writes an error message to the Streamer.bot log.</summary>
        public void LogError(string message)
        {
            _CPH.LogError(Prefix(message));
        }

        /// <summary>
        /// Writes a debug message — only appears when the global var
        /// "osw_DebugMode" is true. Use for verbose diagnostic output
        /// that would be noise during normal operation.
        /// </summary>
        public void LogDebug(string message)
        {
            if (_DebugMode)
                _CPH.LogInfo(Prefix("[DEBUG] " + message));
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        /// <summary>Wraps a message with the tool name prefix.</summary>
        private string Prefix(string message)
        {
            return "[" + _ToolName + "] " + message;
        }
    }
}
