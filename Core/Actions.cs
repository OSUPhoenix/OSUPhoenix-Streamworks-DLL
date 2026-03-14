using System;

namespace OSWTools
{
    /// <summary>
    /// Streamer.bot action execution helpers.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   // Run an action by name — returns true if the action was found and queued
    ///   bool ok = lib.RunAction("My SB Action");
    ///
    ///   // Run and log a warning if the action wasn't found
    ///   lib.RunActionRequired("Next Clip");
    ///
    ///   // Wait a number of milliseconds (interruptible by SB)
    ///   lib.Wait(2000);
    /// </summary>
    public partial class OSWLib
    {
        /// <summary>
        /// Runs a Streamer.bot action by name.
        /// Returns true if the action was found and queued successfully.
        /// Returns false (and logs a warning) if the action doesn't exist.
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
                LogError("RunAction threw an exception for '" + actionName + "': " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Runs a Streamer.bot action and logs an error (not just a warning)
        /// if it isn't found. Use this for actions that are critical to your
        /// tool's operation — makes missing wiring easier to spot in the log.
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
        /// Runs a Streamer.bot action by its internal GUID rather than its name.
        /// Useful when action names might change but the GUID stays stable.
        /// </summary>
        public bool RunActionById(string actionId, bool runImmediately = true)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return false;
            try
            {
                return _CPH.RunActionById(actionId, runImmediately);
            }
            catch (Exception ex)
            {
                LogError("RunActionById threw an exception for '" + actionId + "': " + ex.Message);
                return false;
            }
        }
    }
}
