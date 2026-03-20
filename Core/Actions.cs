using System;

namespace OSWTools
{
    /// <summary>
    /// Streamer.bot action execution helpers.
    ///
    /// NOTE: GetEventType() and GetSource() are intentionally NOT wrapped here.
    /// The EventType and EventSource enums live inside Streamer.bot's own
    /// assemblies and their exact namespaces vary between SB versions.
    /// Call CPH.GetEventType() and CPH.GetSource() directly in your scripts.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///   bool ok = lib.RunAction("Next Clip");
    ///   lib.RunActionRequired("Next Clip");
    ///   lib.Wait(2000);
    /// </summary>
    public partial class OSWLib
    {
        /// <summary>
        /// Runs a Streamer.bot action by name.
        /// Returns true if found and queued. Logs a warning if not found.
        /// </summary>
        public bool RunAction(string actionName, bool runImmediately = true)
        {
            if (string.IsNullOrWhiteSpace(actionName)) return false;
            try
            {
                bool result = _CPH.RunAction(actionName, runImmediately);
                if (!result)
                    LogWarn("RunAction: action not found — '" + actionName + "'");
                return result;
            }
            catch (Exception ex)
            {
                LogError("RunAction threw for '" + actionName + "': " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Runs a required action — logs an error (not just a warning) if not found.
        /// Use for actions that are critical to your tool's operation.
        /// </summary>
        public bool RunActionRequired(string actionName, bool runImmediately = true)
        {
            bool result = RunAction(actionName, runImmediately);
            if (!result)
                LogError("Required action '" + actionName + "' was not found. " +
                         "Create an action with this exact name in Streamer.bot.");
            return result;
        }

        /// <summary>
        /// Pauses execution for the given number of milliseconds.
        /// Uses Streamer.bot's own Wait() so it respects SB's threading model.
        /// </summary>
        public void Wait(int milliseconds)
        {
            if (milliseconds <= 0) return;
            _CPH.Wait(milliseconds);
        }

        /// <summary>
        /// Runs a Streamer.bot action by its internal GUID.
        /// Useful when action names might change but the GUID stays stable.
        /// </summary>
        public bool RunActionById(string actionId, bool runImmediately = true)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return false;
            try { return _CPH.RunActionById(actionId, runImmediately); }
            catch (Exception ex)
            {
                LogError("RunActionById threw for '" + actionId + "': " + ex.Message);
                return false;
            }
        }
    }
}
