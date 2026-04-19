// =============================================================================
// OSWTools — Core/Logging.cs
//
// v1.2.0+: Dual-channel logging.
//
// Every Lib.LogInfo/Warn/Error/Debug call now writes to BOTH:
//   1. Streamer.bot's native log (visible via SB's UI — tagged "[ToolName]")
//   2. The OSW file log at C:\OSW\Logs\OSW_<date>.log (per-tool source column,
//      timestamps, exception traces, 30-day auto-purge)
//
// The routing happens via OSWLogger under the hood. SAS and every other OSW
// tool get the file-log side automatically without changing any call sites.
//
// BACKWARD COMPATIBILITY:
//   - SB-log output for each call is unchanged ("[ToolName] message")
//   - LogDebug still gates on osw_DebugMode global (unchanged semantics)
//   - No changes required to calling code
//
// USAGE (same as before):
//   var lib = new OSWLib(CPH, "Achievement System");
//   lib.LogInfo("Starting up");     // → SB log: "[Achievement System] Starting up"
//                                   //   OSW file: "[INFO] [Achievement System] Starting up"
// =============================================================================

using System;

namespace OSWTools
{
    public partial class OSWLib
    {
        // Tracks whether OSWLogger.Init has been called yet. Because OSWLogger
        // is a static class shared across all OSWLib instances, we only need
        // to initialize it once per SB session. Guarded with a double-check
        // lock to keep the fast path lock-free after the first call.
        private static volatile bool _loggerInitialized = false;
        private static readonly object _loggerInitLock = new object();

        /// <summary>
        /// Writes an informational message. Routes to BOTH Streamer.bot's
        /// native log AND the OSW file log with this tool's name as the source.
        /// </summary>
        public void LogInfo(string message)
        {
            EnsureLoggerInitialized();
            _CPH.LogInfo(Prefix(message));
            OSWLogger.Info(_ToolName, message);
        }

        /// <summary>
        /// Writes a warning. Dual-channel.
        /// </summary>
        public void LogWarn(string message)
        {
            EnsureLoggerInitialized();
            _CPH.LogWarn(Prefix(message));
            OSWLogger.Warn(_ToolName, message);
        }

        /// <summary>
        /// Writes an error. Dual-channel. Optional exception parameter
        /// appends a stack trace to the OSW file log only (keeps SB's log
        /// readable for other developers who don't need your traces).
        /// </summary>
        public void LogError(string message)
        {
            EnsureLoggerInitialized();
            _CPH.LogError(Prefix(message));
            OSWLogger.Error(_ToolName, message);
        }

        /// <summary>
        /// Overload that includes an exception. Only the OSW file log
        /// gets the trace detail — SB's log sees just the message string.
        /// </summary>
        public void LogError(string message, Exception ex)
        {
            EnsureLoggerInitialized();
            _CPH.LogError(Prefix(message));
            OSWLogger.Error(_ToolName, message, ex);
        }

        /// <summary>
        /// Debug message — only appears when the global "osw_DebugMode" is true.
        /// Dual-channel when emitted.
        /// </summary>
        public void LogDebug(string message)
        {
            if (!_DebugMode) return;
            EnsureLoggerInitialized();
            _CPH.LogInfo(Prefix("[DEBUG] " + message));
            OSWLogger.Debug(_ToolName, message);
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures OSWLogger has a CPH reference and has purged old logs.
        /// Runs once per SB session regardless of how many tools log.
        /// Safe to call from the Log* hot path — fast after first call.
        /// </summary>
        private void EnsureLoggerInitialized()
        {
            // Fast path — already done
            if (_loggerInitialized) return;

            lock (_loggerInitLock)
            {
                // Second check inside the lock in case another thread beat us
                if (_loggerInitialized) return;

                try
                {
                    OSWLogger.Init(_CPH);
                    OSWLogger.PurgeOldLogs();
                }
                catch
                {
                    // Never crash the logging path — even if Init fails, we
                    // still set the flag so we don't retry on every call.
                }
                _loggerInitialized = true;
            }
        }

        /// <summary>Wraps a message with the tool name prefix for SB's log.</summary>
        private string Prefix(string message)
        {
            return "[" + _ToolName + "] " + message;
        }
    }
}
